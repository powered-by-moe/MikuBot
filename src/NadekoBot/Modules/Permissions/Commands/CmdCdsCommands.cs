﻿using Discord;
using Discord.Commands;
using Microsoft.EntityFrameworkCore;
using NadekoBot.Attributes;
using NadekoBot.Extensions;
using NadekoBot.Services;
using NadekoBot.Services.Database.Models;
using System.Collections.Concurrent;
using System.Linq;
using System.Threading.Tasks;

namespace NadekoBot.Modules.Permissions
{
    public partial class Permissions
    {

        public class ActiveCooldown
        {
            public string Command { get; set; }
            public ulong UserId { get; set; }
        }

        [Group]
        public class CmdCdsCommands : NadekoSubmodule
        {
            public static ConcurrentDictionary<ulong, ConcurrentHashSet<CommandCooldown>> CommandCooldowns { get; }
            private static ConcurrentDictionary<ulong, ConcurrentHashSet<ActiveCooldown>> activeCooldowns { get; } = new ConcurrentDictionary<ulong, ConcurrentHashSet<ActiveCooldown>>();

            static CmdCdsCommands()
            {
                var configs = NadekoBot.AllGuildConfigs;
                CommandCooldowns = new ConcurrentDictionary<ulong, ConcurrentHashSet<CommandCooldown>>(configs.ToDictionary(k => k.GuildId, v => new ConcurrentHashSet<CommandCooldown>(v.CommandCooldowns)));
            }
            [NadekoCommand, Usage, Description, Aliases]
            [RequireContext(ContextType.Guild)]
            public async Task CmdCooldown(CommandInfo command, int secs)
            {
                var channel = (ITextChannel)Context.Channel;
                if (secs < 0 || secs > 3600)
                {
                    await ReplyErrorLocalized("invalid_second_param_between", 0, 3600).ConfigureAwait(false);
                    return;
                }

                using (var uow = DbHandler.UnitOfWork())
                {
                    var config = uow.GuildConfigs.For(channel.Guild.Id, set => set.Include(gc => gc.CommandCooldowns));
                    var localSet = CommandCooldowns.GetOrAdd(channel.Guild.Id, new ConcurrentHashSet<CommandCooldown>());

                    config.CommandCooldowns.RemoveWhere(cc => cc.CommandName == command.Aliases.First().ToLowerInvariant());
                    localSet.RemoveWhere(cc => cc.CommandName == command.Aliases.First().ToLowerInvariant());
                    if (secs != 0)
                    {
                        var cc = new CommandCooldown()
                        {
                            CommandName = command.Aliases.First().ToLowerInvariant(),
                            Seconds = secs,
                        };
                        config.CommandCooldowns.Add(cc);
                        localSet.Add(cc);
                    }
                    await uow.CompleteAsync().ConfigureAwait(false);
                }
                if (secs == 0)
                {
                    var activeCds = activeCooldowns.GetOrAdd(channel.Guild.Id, new ConcurrentHashSet<ActiveCooldown>());
                    activeCds.RemoveWhere(ac => ac.Command == command.Aliases.First().ToLowerInvariant());
                    await ReplyConfirmLocalized("cmdcd_cleared", 
                        Format.Bold(command.Aliases.First())).ConfigureAwait(false);
                }
                else
                {
                    await ReplyConfirmLocalized("cmdcd_add", 
                        Format.Bold(command.Aliases.First()), 
                        Format.Bold(secs.ToString())).ConfigureAwait(false);
                }
            }

            [NadekoCommand, Usage, Description, Aliases]
            [RequireContext(ContextType.Guild)]
            public async Task AllCmdCooldowns()
            {
                var channel = (ITextChannel)Context.Channel;
                var localSet = CommandCooldowns.GetOrAdd(channel.Guild.Id, new ConcurrentHashSet<CommandCooldown>());

                if (!localSet.Any())
                    await ReplyConfirmLocalized("cmdcd_none").ConfigureAwait(false);
                else
                    await channel.SendTableAsync("", localSet.Select(c => c.CommandName + ": " + c.Seconds + GetText("sec")), s => $"{s,-30}", 2).ConfigureAwait(false);
            }

            public static bool HasCooldown(CommandInfo cmd, IGuild guild, IUser user)
            {
                if (guild == null)
                    return false;
                var cmdcds = CmdCdsCommands.CommandCooldowns.GetOrAdd(guild.Id, new ConcurrentHashSet<CommandCooldown>());
                CommandCooldown cdRule;
                if ((cdRule = cmdcds.FirstOrDefault(cc => cc.CommandName == cmd.Aliases.First().ToLowerInvariant())) != null)
                {
                    var activeCdsForGuild = activeCooldowns.GetOrAdd(guild.Id, new ConcurrentHashSet<ActiveCooldown>());
                    if (activeCdsForGuild.FirstOrDefault(ac => ac.UserId == user.Id && ac.Command == cmd.Aliases.First().ToLowerInvariant()) != null)
                    {
                        return true;
                    }
                    activeCdsForGuild.Add(new ActiveCooldown()
                    {
                        UserId = user.Id,
                        Command = cmd.Aliases.First().ToLowerInvariant(),
                    });
                    var _ = Task.Run(async () =>
                    {
                        try
                        {
                            await Task.Delay(cdRule.Seconds * 1000);
                            activeCdsForGuild.RemoveWhere(ac => ac.Command == cmd.Aliases.First().ToLowerInvariant() && ac.UserId == user.Id);
                        }
                        catch
                        {
                            // ignored
                        }
                    });
                }
                return false;
            }
        }
    }
}
