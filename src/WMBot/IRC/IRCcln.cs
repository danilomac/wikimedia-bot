//This program is free software: you can redistribute it and/or modify
//it under the terms of the GNU General Public License as published by
//the Free Software Foundation, either version 3 of the License, or
//(at your option) any later version.

//This program is distributed in the hope that it will be useful,
//but WITHOUT ANY WARRANTY; without even the implied warranty of
//MERCHANTABILITY or FITNESS FOR A PARTICULAR PURPOSE.  See the
//GNU General Public License for more details.

// Created by Petr Bena

using System;
using System.Collections.Generic;
using System.IO;
using System.Net.Sockets;
using System.Text;
using System.Threading;

namespace wmib
{
    public partial class IRC
    {
        /// <summary>
        /// Instance that owns this handler
        /// </summary>
        public Instance ParentInstance = null;
        /// <summary>
        /// If false is returned it means this handler is defunct - it may be connected, but it's still
        /// logging in or server didn't finish loading up
        /// </summary>
        public bool IsWorking = false;
        public string Bouncer = "127.0.0.1";
        /// <summary>
        /// Whether bot has already joined all channels after connection to irc
        /// </summary>
        public bool ChannelsJoined = false;
        /// <summary>
        /// Server addr
        /// </summary>
        private readonly string Server;
        /// <summary>
        /// Port to bouncer
        /// </summary>
        public int BouncerPort = 6667;
        /// <summary>
        /// Nick
        /// </summary>
        public string NickName;
        /// <summary>
        /// ID
        /// </summary>
        private readonly string Ident;
        /// <summary>
        /// User
        /// </summary>
        private readonly string UserName;
        /// <summary>
        /// Socket
        /// </summary>
        private static NetworkStream networkStream;
        /// <summary>
        /// Socket Reader
        /// </summary>
        private StreamReader streamReader;
        /// <summary>
        /// Writer
        /// </summary>
        private StreamWriter streamWriter;
        /// <summary>
        /// Pinger
        /// </summary>
        private static Thread PingerThread;
        /// <summary>
        /// Queue thread
        /// </summary>
        private Thread _Queue;
        /// <summary>
        /// This is a thread for channel list
        /// </summary>
        public Thread ChannelThread;
        /// <summary>
        /// Queue of all messages that should be delivered to network
        /// </summary>
        public MessageQueue Queue = null;
        private bool connected;
        private readonly List<string> Backlog = new List<string>();
        /// <summary>
        /// If network is connected
        /// </summary>
        public bool IsConnected
        {
            get
            {
                return connected;
            }
        }

        /// <summary>
        /// User modes, these are modes that are applied on network, not channel (invisible, oper)
        /// </summary>
        public List<char> UModes = new List<char> { 'i', 'w', 'o', 'Q', 'r', 'A' };
        /// <summary>
        /// Channel user symbols (oper and such)
        /// </summary>
        public List<char> UChars = new List<char> { '~', '&', '@', '%', '+' };
        /// <summary>
        /// Channel user modes (voiced, op)
        /// </summary>
        public List<char> CUModes = new List<char> { 'q', 'a', 'o', 'h', 'v' };
        /// <summary>
        /// Channel modes (moderated, topic)
        /// </summary>
        public List<char> CModes = new List<char> { 'n', 'r', 't', 'm' };
        /// <summary>
        /// Special channel modes with parameter as a string
        /// </summary>
        public List<char> SModes = new List<char> { 'k', 'L' };
        /// <summary>
        /// Special channel modes with parameter as a number
        /// </summary>
        public List<char> XModes = new List<char> { 'l' };
        /// <summary>
        /// Special channel user modes with parameters as a string
        /// </summary>
        public List<char> PModes = new List<char> { 'b', 'I', 'e' };

        /// <summary>
        /// Creates a new instance of IRC
        /// </summary>
        /// <param name="_server">Server to connect to</param>
        /// <param name="_nick">Nickname to use</param>
        /// <param name="_ident">Ident</param>
        /// <param name="_username">Username</param>
        /// <param name="_instance">Instance</param>
        public IRC(string _server, string _nick, string _ident, string _username, Instance _instance)
        {
            Server = _server;
            Queue = new MessageQueue(this);
            UserName = _username;
            NickName = _nick;
            Ident = _ident;
            ParentInstance = _instance;
        }

        /// <summary>
        /// Send a message to channel
        /// </summary>
        /// <param name="message">Message</param>
        /// <param name="channel">Channel</param>
        /// <returns></returns>
        public bool Message(string message, Channel channel)
        {
            try
            {
                if (channel.Suppress)
                {
                    return true;
                }
                SendData("PRIVMSG " + channel.Name + " :" + message.Replace("\n", " "));
                lock (ExtensionHandler.Extensions)
                {
                    foreach (Module module in ExtensionHandler.Extensions)
                    {
                        try
                        {
                            if (module.IsWorking)
                            {
                                module.Hook_OnSelf(channel, new User(Configuration.IRC.NickName, "wikimedia/bot/wm-bot", "wmib"), message);
                            }
                        }
                        catch (Exception fail)
                        {
                            Core.HandleException(fail, module.Name);
                        }
                    }
                }
            }
            catch (Exception fail)
            {
                Core.HandleException(fail);
            }
            return true;
        }

        /// <summary>
        /// Send a message to channel
        /// </summary>
        /// <param name="message">Message</param>
        /// <param name="channel">Channel</param>
        /// <returns></returns>
        public bool Message(string message, string channel)
        {
            try
            {
                Channel curr = Core.GetChannel(channel);
                if (curr == null && channel.StartsWith("#"))
                {
                    Syslog.WarningLog("Attempt to send a message to non existing channel: " + channel + " " + message);
                    return true;
                }
                if (curr != null && curr.Suppress)
                {
                    return true;
                }
                SendData("PRIVMSG " + channel + " :" + message.Replace("\n", " "));
                lock (ExtensionHandler.Extensions)
                {
                    foreach (Module module in ExtensionHandler.Extensions)
                    {
                        try
                        {
                            if (module.IsWorking)
                            {
                                module.Hook_OnSelf(curr, new User(Configuration.IRC.NickName, "wikimedia/bot/wm-bot", "wmib"), message);
                            }
                        }
                        catch (Exception fail)
                        {
                            Core.HandleException(fail, module.Name);
                        }
                    }
                }
            }
            catch (Exception fail)
            {
                Core.HandleException(fail);
            }
            return true;
        }

        /// <summary>
        /// Join a channel
        /// </summary>
        /// <param name="Channel">Channel</param>
        /// <returns></returns>
        public bool Join(Channel Channel)
        {
            if (Channel != null)
            {
                if (Channel.PrimaryInstance != ParentInstance)
                {
                    Syslog.DebugLog("Fixing instance for " + Channel.Name);
                    Channel.PrimaryInstance.irc.Join(Channel);
                    return false;
                }
                SendData("JOIN " + Channel.Name);
                return true;
            }
            return false;
        }

        /// <summary>
        /// Restart a message delivery
        /// </summary>
        public void RestartIRCMessageDelivery()
        {
            Core.ThreadManager.KillThread(_Queue);
            lock(this.Queue.newmessages)
            {
                this.Queue.newmessages.Clear();
            }
            this._Queue = new Thread(Queue.Run);
            Core.ThreadManager.RegisterThread(_Queue);
            this.Queue.Messages.Clear();
            this._Queue.Start();
        }

        /// <summary>
        /// This function will retrieve a list of users in a channel for every channel that doesn't have it so far
        /// </summary>
        public void ChannelList()
        {
            try
            {
                while (IsConnected && Core.IsRunning)
                {
                    List<string> channels = new List<string>();
                    // check if there is some channel which needs an update of user list
                    foreach (Channel dd in ParentInstance.ChannelList)
                    {
                        if (!dd.HasFreshUserList)
                        {
                            channels.Add(dd.Name);
                        }
                    }

                    if (channels.Count >= 1)
                    {
                        foreach (string xx in channels)
                        {
                            Syslog.Log("requesting user list on " + ParentInstance.Nick + " for channel: " + xx);
                            // Send the request with low priority
                            Queue.Send("WHO " + xx, priority.low);
                        }
                        // we give 10 seconds for each channel to send us a list
                        Thread.Sleep(10000 * channels.Count);
                    }
                    Thread.Sleep(10000);
                }
            }
            catch (ThreadAbortException)
            {
                Core.ThreadManager.UnregisterThread(Thread.CurrentThread);
                return;
            }
            catch (Exception fail)
            {
                Core.HandleException(fail);
            }
            Core.ThreadManager.UnregisterThread(Thread.CurrentThread);
        }

        /// <summary>
        /// This is running in a separate thread
        /// </summary>
        private void Ping()
        {
            while (IsConnected && Core.IsRunning)
            {
                try
                {
                    Thread.Sleep(20000);
                    if (!Configuration.IRC.UsingBouncer)
                    {
                        SendData("PING :" + Configuration.IRC.NetworkHost);
                    }
                }
                catch (ThreadAbortException)
                {
                    Core.ThreadManager.UnregisterThread(Thread.CurrentThread);
                    return;
                }
                catch (Exception fail)
                {
                    Core.HandleException(fail);
                }
            }
            Core.ThreadManager.UnregisterThread(Thread.CurrentThread);
        }

        public void Disconnect()
        {
            if (!IsConnected)
            {
                return;
            }
            Syslog.DebugLog("Closing connection for " + NickName);
            connected = false;
            this.ChannelsJoined = false;
            if (_Queue != null)
            {
                Core.ThreadManager.KillThread(_Queue);
            }
            if (networkStream != null)
            {
                networkStream.Close();
            }
            if (streamReader != null)
            {
                streamReader.Close();
            }
            if (streamWriter != null)
            {
                streamWriter.Close();
            }
            IsWorking = false;
            if (ChannelThread != null)
            {
                Core.ThreadManager.KillThread(ChannelThread);
            }
            if (PingerThread != null)
            {
                Core.ThreadManager.KillThread(PingerThread);
            }
        }

        /// <summary>
        /// Connection
        /// </summary>
        /// <returns></returns>
        public bool Reconnect()
        {
            if (!Core.IsRunning)
            {
                Syslog.Log("Ignoring request to reconnect because bot is shutting down");
                return false;
            }
            Disconnect();
            Connect();
            return true;
        }

        /// <summary>
        /// Send data to network
        /// </summary>
        /// <param name="text"></param>
        public void SendData(string text)
        {
            if (!Core.IsRunning)
            {
                return;
            }
            if (IsConnected)
            {
                lock (this)
                {
                    streamWriter.WriteLine(text);
                    streamWriter.Flush();
                    Core.TrafficLog(ParentInstance.Nick + ">>>>>>" + text);
                }
            }
            else
            {
                Syslog.Log("DEBUG: didn't send data to network, because it's not connected");
            }
        }

        /// <summary>
        /// Identify
        /// </summary>
        /// <returns></returns>
        public bool Authenticate()
        {
            if (Configuration.IRC.LoginPw != "")
            {
                SendData("PRIVMSG nickserv :identify " + Configuration.IRC.LoginNick + " " + Configuration.IRC.LoginPw);
                Thread.Sleep(4000);
            }
            return true;
        }

        /// <summary>
        /// Connect this instance
        /// </summary>
        public void Connect()
        {
            Syslog.Log("Connecting instance " + NickName + " to irc server " + Server + "...");
            if (!Configuration.IRC.UsingBouncer)
            {
                networkStream = new TcpClient(Server, 6667).GetStream();
            } else
            {
                Syslog.Log(ParentInstance.Nick + " is using personal bouncer port " + BouncerPort);
                networkStream = new TcpClient(Bouncer, BouncerPort).GetStream();
            }
            connected = true;
            streamReader = new StreamReader(networkStream, Encoding.UTF8);
            streamWriter = new StreamWriter(networkStream);

            bool Auth = true;

            if (Configuration.IRC.UsingBouncer)
            {
                SendData("CONTROL: STATUS");
                Syslog.Log("CACHE: Waiting for buffer (network bouncer) of instance " + this.ParentInstance.Nick);
                bool done = true;
                while (done)
                {
                    string response = streamReader.ReadLine();
                    if (response == "CONTROL: TRUE")
                    {
                        Syslog.DebugLog("Resumming previous session on " + this.ParentInstance.Nick);
                        done = false;
                        Auth = false;
                        ChannelsJoined = true;
                        IsWorking = true;
                    } else if (response.StartsWith(":"))
                    {
                        Backlog.Add(response);
                    } else if (response == "CONTROL: FALSE")
                    {
                        Syslog.DebugLog("Bouncer is not connected, starting new session on " + this.ParentInstance.Nick);
                        done = false;
                        ChannelsJoined = false;
                        SendData("CONTROL: CREATE " + Server);
                        streamWriter.Flush();
                    }
                }
            }

            if (Auth)
            {
                NetworkInit();
            }

            _Queue = new Thread(Queue.Run) {Name = "MessageQueue:" + NickName};
            Core.ThreadManager.RegisterThread(_Queue);
            PingerThread = new Thread(Ping);
            Core.ThreadManager.RegisterThread(PingerThread);
            PingerThread.Name = "Ping:" + NickName;
            PingerThread.Start();
            _Queue.Start();
        }

        private void NetworkInit()
        {
            SendData("USER " + UserName + " 8 * :" + Ident);
            SendData("NICK " + ParentInstance.Nick);

            Authenticate();
        }

        /// <summary>
        /// Connection
        /// </summary>
        /// <returns></returns>
        public void ParserExec()
        {
            string nick = "";
            string host = "";
            const char delimiter = (char)001;

            while ((!streamReader.EndOfStream || Backlog.Count > 0) && Core.IsRunning)
            {
                string text;
                lock (Backlog)
                {
                    if (Backlog.Count == 0)
                    {
                        text = streamReader.ReadLine();
                    } else
                    {
                        text = Backlog[0];
                        Backlog.RemoveAt(0);
                    }
                }
                Core.TrafficLog(ParentInstance.Nick + "<<<<<<" + text);
                if (Configuration.IRC.UsingBouncer)
                {
                    if (text.StartsWith("CONTROL: "))
                    {
                        if (text == "CONTROL: DC")
                        {
                            SendData("CONTROL: CREATE " + Server);
                            streamWriter.Flush();
                            Syslog.Log("CACHE: Lost connection to remote on " + this.ParentInstance.Nick + 
                                ", creating new session on remote"
                            );
                            ChannelsJoined = false;
                            IsWorking = false;
                            int xx = 0;
                            bool Connected_ = false;
                            while (!Connected_)
                            {
                                Thread.Sleep(2000);
                                SendData("CONTROL: STATUS");
                                string response = streamReader.ReadLine();
                                Core.TrafficLog(ParentInstance.Nick + "<<<<<<" + response);
                                if (response.StartsWith(":"))
                                {
                                    // we received network data here
                                    lock(Backlog)
                                    {
                                        Backlog.Add(response);
                                    }
                                    continue;
                                }
                                if (response == "CONTROL: TRUE")
                                {
                                    Syslog.Log("Bouncer reconnected to network on: " + NickName);
                                    NetworkInit();
                                    ParentInstance.Join();
                                    Connected_ = true;
                                } else
                                {
                                    xx++;
                                    if (xx > 6)
                                    {
                                        Syslog.WarningLog("Bouncer failed to connect to the network within 10 seconds, disconnecting it: " + NickName);
                                        SendData("CONTROL: DISCONNECT");
                                        return;
                                    }
                                    Syslog.Log("Still waiting for bouncer (trying " + xx 
                                               + "/6) on " + NickName + " " + response);
                                }
                            }
                        }
                    }
                }
                if (text.StartsWith(":"))
                {
                    ProcessorIRC processor = new ProcessorIRC(text) {instance = ParentInstance};
                    processor.Result();
                    string check = text.Substring(text.IndexOf(" "));
                    if (!check.StartsWith(" 005"))
                    {
                        string command = "";
                        if (text.Contains(" :"))
                        {
                            command = text.Substring(1);
                            command = command.Substring(0, command.IndexOf(" :"));
                        }
                        if (command.Contains("PRIVMSG"))
                        {
                            string info = text.Substring(1, text.IndexOf(" :", 1) - 1);
                            // we got a message here :)
                            if (text.Contains("!") && text.Contains("@"))
                            {
                                nick = info.Substring(0, info.IndexOf("!"));
                                host = info.Substring(info.IndexOf("@") + 1, info.IndexOf(" ", info.IndexOf("@")) - 1 - info.IndexOf("@"));
                            }
                            string info_host = info.Substring(info.IndexOf("PRIVMSG "));

                            string message;
                            if (info_host.Contains("#"))
                            {
                                string channel = info_host.Substring(info_host.IndexOf("#"));
                                if (channel == Configuration.System.DebugChan && ParentInstance.Nick != Core.irc.NickName)
                                {
                                    continue;
                                }
                                message = text.Replace(info, "");
                                message = message.Substring(message.IndexOf(" :") + 2);
                                if (message.Contains(delimiter + "ACTION"))
                                {
                                    Core.GetAction(message.Replace(delimiter + "ACTION", ""), 
                                                               channel, host, nick);
                                    continue;
                                }
                                Core.GetMessage(channel, nick, host, message);
                                continue;
                            }
                            message = text.Substring(text.IndexOf("PRIVMSG"));
                            message = message.Substring(message.IndexOf(" :"));
                            // private message
                            if (message.StartsWith(" :" + delimiter + "FINGER"))
                            {
                                SendData("NOTICE " + nick + " :" + delimiter + "FINGER" + 
                                    " I am a bot don't finger me"
                                );
                                continue;
                            }
                            if (message.StartsWith(" :" + delimiter + "TIME"))
                            {
                                SendData("NOTICE " + nick + " :" + delimiter + "TIME " + 
                                    DateTime.Now
                                );
                                continue;
                            }
                            if (message.StartsWith(" :" + delimiter + "PING"))
                            {
                                SendData("NOTICE " + nick + " :" + delimiter + "PING" + message.Substring(
                                                message.IndexOf(delimiter + "PING") + 5)
                                );
                                continue;
                            }
                            if (message.StartsWith(" :" + delimiter + "VERSION"))
                            {
                                SendData("NOTICE " + nick + " :" + delimiter + "VERSION " 
                                    + Configuration.System.Version
                                );
                                continue;
                            }
                            // store which instance this message was from so that we can send it using same instance
                            lock(Core.TargetBuffer)
                            {
                                if (!Core.TargetBuffer.ContainsKey(nick))
                                {
                                    Core.TargetBuffer.Add(nick, ParentInstance);
                                } else
                                {
                                    Core.TargetBuffer[nick] = ParentInstance;
                                }
                            }
                            bool respond = true;
                            string modules = "";
                            lock(ExtensionHandler.Extensions)
                            {
                                foreach (Module module in ExtensionHandler.Extensions)
                                {
                                    if (module.IsWorking)
                                    {
                                        try
                                        {

                                            if (module.Hook_OnPrivateFromUser(message.Substring(2), 
                                                                                new User(nick, host, Ident)))
                                            {
                                                respond = false;
                                                modules += module.Name + " ";
                                            }
                                        } catch (Exception fail)
                                        {
                                            Core.HandleException(fail);
                                        }
                                    }
                                }
                            }
                            if (respond)
                            {
                                Queue.DeliverMessage("Hi, I am robot, this command was not understood." +
                                    " Please bear in mind that every message you send" +
                                    " to me will be logged for debuging purposes. See" +
                                    " documentation at http://meta.wikimedia.org/wiki" +
                                    "/WM-Bot for explanation of commands", nick,
                                                                      priority.low);
                                Syslog.Log("Ignoring private message: (" + nick + ") " + message.Substring(2), false);
                            } else
                            {
                                Syslog.Log("Private message: (handled by " + modules + " from " + nick + ") " + 
                                    message.Substring(2), false);
                            }
                            continue;
                        }
                        if (command.Contains("PING "))
                        {
                            SendData("PONG " + text.Substring(text.IndexOf("PING ") + 5));
                            Console.WriteLine(command);
                        }
                        if (command.Contains("KICK"))
                        {
                            string temp = command.Substring(command.IndexOf("KICK"));
                            string[] parts = temp.Split(' ');
                            if (parts.Length > 1)
                            {
                                string _channel = parts[1];
                                if (_channel == Configuration.System.DebugChan && ParentInstance.Nick != Core.irc.NickName)
                                {
                                    continue;
                                }
                                string user = parts[2];
                                if (user == NickName)
                                {
                                    Channel chan = Core.GetChannel(_channel);
                                    if (chan != null)
                                    {
                                        lock(Configuration.Channels)
                                        {
                                            if (Configuration.Channels.Contains(chan))
                                            {
                                                Configuration.Channels.Remove(chan);
                                                Syslog.Log("I was kicked from " + parts[1]);
                                                Configuration.Save();
                                            }
                                        }
                                    }
                                }
                            }
                        }
                    }
                }
                Thread.Sleep(50);
            }
            Syslog.Log("Lost connection to IRC on " + NickName);
            Disconnect();
            IsWorking = false;
        }

        /// <summary>
        /// Priority of message
        /// </summary>
        public enum priority
        {
            /// <summary>
            /// Low
            /// </summary>
            low = 1,
            /// <summary>
            /// Normal
            /// </summary>
            normal = 2,
            /// <summary>
            /// High
            /// </summary>
            high = 3,
        }
    }
}
