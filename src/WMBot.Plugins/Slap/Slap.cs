namespace wmib
{
    public class Slap : Module
    {
        public override bool Construct()
        {
            HasSeparateThreadInstance = false;
            Name = "Slap";
            Version = "1.0";
            return true;
        }

        public override void Hook_PRIV(Channel channel, User invoker, string message)
        {
            if (!message.StartsWith(Configuration.System.CommandPrefix) && GetConfig(channel, "Slap.Enabled", false))
            {
                string ms = message.Trim();
                ms = ms.Replace("!", "");
                ms = ms.Replace("?", "");
                ms = ms.ToLower();
                if (ms.StartsWith("hi "))
                {
                    ms = ms.Substring(3);
                }
                if (ms.StartsWith("hi, "))
                {
                    ms = ms.Substring(4);
                }
                if (ms.StartsWith("hello "))
                {
                    ms = ms.Substring(5);
                }
                if (ms.StartsWith("hello, "))
                {
                    ms = ms.Substring(6);
                }
                if (ms.EndsWith(":ping") || ms.EndsWith(": ping"))
                {
                    string target = message.Substring(0, message.IndexOf(":"));
                    if (GetConfig(channel, "Slap.Ping." + target, false))
                    {
                        channel.PrimaryInstance.irc.Queue.DeliverMessage("Hi " + invoker.Nick + ", you just managed to say pointless nick: ping. Now please try again with some proper meaning of your request, something like nick: I need this and that. Or don't do that at all, it's very annoying. Thank you", channel);
                        return;
                    }
                }

                if (ms == "i have a question" || ms == "can i ask a question" || ms == "can i ask" || ms == "i got a question" || ms == "can i have a question" || ms == "can someone help me" || ms == "i need help")
                {
                    channel.PrimaryInstance.irc.Queue.DeliverMessage("Hi " + invoker.Nick + ", just ask! There is no need to ask if you can ask", channel);
                    return;
                }

                if (ms == "is anyone here" || ms == "is anybody here" || ms == "is anybody there" || ms == "is some one there" || ms == "is someone there" || ms == "is someone here")
                {
                    channel.PrimaryInstance.irc.Queue.DeliverMessage("Hi " + invoker.Nick + ", I am here, if you need anything, please ask, otherwise no one is going to help you... Thank you", channel);
                    return;
                }
            }

            if (message == Configuration.System.CommandPrefix + "slap")
            {
                if (channel.SystemUsers.IsApproved(invoker, "admin"))
                {
                    SetConfig(channel, "Slap.Enabled", true);
                    Core.irc.Queue.DeliverMessage("I will be slapping stupid people since now", channel);
                    channel.SaveConfig();
                    return;
                }
                if (!channel.SuppressWarnings)
                {
                    Core.irc.Queue.DeliverMessage("Permission denied", channel);
                }
            }

            if (message == Configuration.System.CommandPrefix + "noslap")
            {
                if (channel.SystemUsers.IsApproved(invoker, "admin"))
                {
                    SetConfig(channel, "Slap.Enabled", false);
                    Core.irc.Queue.DeliverMessage("I will not be slapping stupid people since now", channel);
                    channel.SaveConfig();
                    return;
                }
                if (!channel.SuppressWarnings)
                {
                    Core.irc.Queue.DeliverMessage("Permission denied", channel);
                }
            }

            if (message == Configuration.System.CommandPrefix + "nopingslap")
            {
                if (channel.SystemUsers.IsApproved(invoker, "trust"))
                {
                    SetConfig(channel, "Slap.Ping." + invoker.Nick.ToLower(), false);
                    Core.irc.Queue.DeliverMessage("I will not be slapping people who slap you now", channel);
                    channel.SaveConfig();
                    return;
                }
                if (!channel.SuppressWarnings)
                {
                    Core.irc.Queue.DeliverMessage("Permission denied", channel);
                }
            }

            if (message == Configuration.System.CommandPrefix + "pingslap")
            {
                if (channel.SystemUsers.IsApproved(invoker, "trust"))
                {
                    SetConfig(channel, "Slap.Ping." + invoker.Nick.ToLower(), true);
                    Core.irc.Queue.DeliverMessage("I will be slapping people who ping you now", channel);
                    channel.SaveConfig();
                    return;
                }
                if (!channel.SuppressWarnings)
                {
                    Core.irc.Queue.DeliverMessage("Permission denied", channel);
                }
            }
        }
    }
}
