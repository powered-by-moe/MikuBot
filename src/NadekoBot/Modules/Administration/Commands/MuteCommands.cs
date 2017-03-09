﻿using Discord;
using Discord.Commands;
using Microsoft.EntityFrameworkCore;
using NadekoBot.Attributes;
using NadekoBot.Extensions;
using NadekoBot.Services;
using NadekoBot.Services.Database.Models;
using NLog;
using System;
using System.Collections.Concurrent;
using System.Linq;
using System.Threading.Tasks;

namespace NadekoBot.Modules.Administration
{
    public partial class Administration
    {
        [Group]
        public class MuteCommands : NadekoSubmodule
        {
            private static ConcurrentDictionary<ulong, string> guildMuteRoles { get; }
            private static ConcurrentDictionary<ulong, ConcurrentHashSet<ulong>> mutedUsers { get; }

            public static event Action<IGuildUser, MuteType> UserMuted = delegate { };
            public static event Action<IGuildUser, MuteType> UserUnmuted = delegate { };


            public enum MuteType {
                Voice,
                Chat,
                All
            }

            static MuteCommands()
            {
                var configs = NadekoBot.AllGuildConfigs;
                guildMuteRoles = new ConcurrentDictionary<ulong, string>(configs
                        .Where(c => !string.IsNullOrWhiteSpace(c.MuteRoleName))
                        .ToDictionary(c => c.GuildId, c => c.MuteRoleName));

                mutedUsers = new ConcurrentDictionary<ulong, ConcurrentHashSet<ulong>>(configs.ToDictionary(
                    k => k.GuildId,
                    v => new ConcurrentHashSet<ulong>(v.MutedUsers.Select(m => m.UserId))
                ));

                NadekoBot.Client.UserJoined += Client_UserJoined;
            }

            private static async Task Client_UserJoined(IGuildUser usr)
            {
                try
                {
                    ConcurrentHashSet<ulong> muted;
                    mutedUsers.TryGetValue(usr.Guild.Id, out muted);

                    if (muted == null || !muted.Contains(usr.Id))
                        return;
                    await MuteUser(usr).ConfigureAwait(false);
                }
                catch (Exception ex)
                {
                    LogManager.GetCurrentClassLogger().Warn(ex);
                }
                    
            }

            public static async Task MuteUser(IGuildUser usr)
            {
                await usr.ModifyAsync(x => x.Mute = true).ConfigureAwait(false);
                await usr.AddRolesAsync(await GetMuteRole(usr.Guild)).ConfigureAwait(false);
                using (var uow = DbHandler.UnitOfWork())
                {
                    var config = uow.GuildConfigs.For(usr.Guild.Id, set => set.Include(gc => gc.MutedUsers));
                    config.MutedUsers.Add(new MutedUserId()
                    {
                        UserId = usr.Id
                    });
                    ConcurrentHashSet<ulong> muted;
                    if (mutedUsers.TryGetValue(usr.Guild.Id, out muted))
                        muted.Add(usr.Id);
                    
                    await uow.CompleteAsync().ConfigureAwait(false);
                }
                UserMuted(usr, MuteType.All);
            }

            public static async Task UnmuteUser(IGuildUser usr)
            {
                await usr.ModifyAsync(x => x.Mute = false).ConfigureAwait(false);
                await usr.RemoveRolesAsync(await GetMuteRole(usr.Guild)).ConfigureAwait(false);
                using (var uow = DbHandler.UnitOfWork())
                {
                    var config = uow.GuildConfigs.For(usr.Guild.Id, set => set.Include(gc => gc.MutedUsers));
                    config.MutedUsers.Remove(new MutedUserId()
                    {
                        UserId = usr.Id
                    });
                    ConcurrentHashSet<ulong> muted;
                    if (mutedUsers.TryGetValue(usr.Guild.Id, out muted))
                        muted.TryRemove(usr.Id);
                    await uow.CompleteAsync().ConfigureAwait(false);
                }
                UserUnmuted(usr, MuteType.All);
            }

            public static async Task<IRole>GetMuteRole(IGuild guild)
            {
                const string defaultMuteRoleName = "nadeko-mute";

                var muteRoleName = guildMuteRoles.GetOrAdd(guild.Id, defaultMuteRoleName);

                var muteRole = guild.Roles.FirstOrDefault(r => r.Name == muteRoleName);
                if (muteRole == null)
                {

                    //if it doesn't exist, create it 
                    try { muteRole = await guild.CreateRoleAsync(muteRoleName, GuildPermissions.None).ConfigureAwait(false); }
                    catch
                    {
                        //if creations fails,  maybe the name is not correct, find default one, if doesn't work, create default one
                        muteRole = guild.Roles.FirstOrDefault(r => r.Name == muteRoleName) ??
                            await guild.CreateRoleAsync(defaultMuteRoleName, GuildPermissions.None).ConfigureAwait(false);
                    }

                    foreach (var toOverwrite in (await guild.GetTextChannelsAsync()))
                    {
                        try
                        {
                            await toOverwrite.AddPermissionOverwriteAsync(muteRole, new OverwritePermissions(sendMessages: PermValue.Deny, attachFiles: PermValue.Deny))
                                    .ConfigureAwait(false);
                        }
                        catch
                        {
                            // ignored
                        }
                        await Task.Delay(200).ConfigureAwait(false);
                    }
                }
                return muteRole;
            }

            [NadekoCommand, Usage, Description, Aliases]
            [RequireContext(ContextType.Guild)]
            [RequireUserPermission(GuildPermission.ManageRoles)]
            [Priority(1)]
            public async Task SetMuteRole([Remainder] string name)
            {
                name = name.Trim();
                if (string.IsNullOrWhiteSpace(name))
                    return;

                using (var uow = DbHandler.UnitOfWork())
                {
                    var config = uow.GuildConfigs.For(Context.Guild.Id, set => set);
                    config.MuteRoleName = name;
                    guildMuteRoles.AddOrUpdate(Context.Guild.Id, name, (id, old) => name);
                    await uow.CompleteAsync().ConfigureAwait(false);
                }
                await ReplyConfirmLocalized("mute_role_set").ConfigureAwait(false);
            }

            [NadekoCommand, Usage, Description, Aliases]
            [RequireContext(ContextType.Guild)]
            [RequireUserPermission(GuildPermission.ManageRoles)]
            [Priority(0)]
            public Task SetMuteRole([Remainder] IRole role)
                => SetMuteRole(role.Name);

            [NadekoCommand, Usage, Description, Aliases]
            [RequireContext(ContextType.Guild)]
            [RequireUserPermission(GuildPermission.ManageRoles)]
            [RequireUserPermission(GuildPermission.MuteMembers)]
            public async Task Mute(IGuildUser user)
            {
                try
                {
                    await MuteUser(user).ConfigureAwait(false);
                    await ReplyConfirmLocalized("user_muted", Format.Bold(user.ToString())).ConfigureAwait(false);
                }
                catch
                {
                    await ReplyErrorLocalized("mute_error").ConfigureAwait(false);
                }
            }

            [NadekoCommand, Usage, Description, Aliases]
            [RequireContext(ContextType.Guild)]
            [RequireUserPermission(GuildPermission.ManageRoles)]
            [RequireUserPermission(GuildPermission.MuteMembers)]
            public async Task Unmute(IGuildUser user)
            {
                try
                {
                    await UnmuteUser(user).ConfigureAwait(false);
                    await ReplyConfirmLocalized("user_unmuted", Format.Bold(user.ToString())).ConfigureAwait(false);
                }
                catch
                {
                    await ReplyErrorLocalized("mute_error").ConfigureAwait(false);
                }
            }

            [NadekoCommand, Usage, Description, Aliases]
            [RequireContext(ContextType.Guild)]
            [RequireUserPermission(GuildPermission.ManageRoles)]
            public async Task ChatMute(IGuildUser user)
            {
                try
                {
                    await user.AddRolesAsync(await GetMuteRole(Context.Guild).ConfigureAwait(false)).ConfigureAwait(false);
                    UserMuted(user, MuteType.Chat);
                    await ReplyConfirmLocalized("user_chat_mute", Format.Bold(user.ToString())).ConfigureAwait(false);
                }
                catch
                {
                    await ReplyErrorLocalized("mute_error").ConfigureAwait(false);
                }
            }

            [NadekoCommand, Usage, Description, Aliases]
            [RequireContext(ContextType.Guild)]
            [RequireUserPermission(GuildPermission.ManageRoles)]
            public async Task ChatUnmute(IGuildUser user)
            {
                try
                {
                    await user.RemoveRolesAsync(await GetMuteRole(Context.Guild).ConfigureAwait(false)).ConfigureAwait(false);
                    UserUnmuted(user, MuteType.Chat);
                    await ReplyConfirmLocalized("user_chat_unmute", Format.Bold(user.ToString())).ConfigureAwait(false);
                }
                catch
                {
                    await ReplyErrorLocalized("mute_error").ConfigureAwait(false);
                }
            }

            [NadekoCommand, Usage, Description, Aliases]
            [RequireContext(ContextType.Guild)]
            [RequireUserPermission(GuildPermission.MuteMembers)]
            public async Task VoiceMute(IGuildUser user)
            {
                try
                {
                    await user.ModifyAsync(usr => usr.Mute = true).ConfigureAwait(false);
                    UserMuted(user, MuteType.Voice);
                    await ReplyConfirmLocalized("user_voice_mute", Format.Bold(user.ToString())).ConfigureAwait(false);
                }
                catch
                {
                    await ReplyErrorLocalized("mute_error").ConfigureAwait(false);
                }
            }

            [NadekoCommand, Usage, Description, Aliases]
            [RequireContext(ContextType.Guild)]
            [RequireUserPermission(GuildPermission.MuteMembers)]
            public async Task VoiceUnmute(IGuildUser user)
            {
                try
                {
                    await user.ModifyAsync(usr => usr.Mute = false).ConfigureAwait(false);
                    UserUnmuted(user, MuteType.Voice);
                    await ReplyConfirmLocalized("user_voice_unmute", Format.Bold(user.ToString())).ConfigureAwait(false);
                }
                catch
                {
                    await ReplyErrorLocalized("mute_error").ConfigureAwait(false);
                }
            }
        }
    }
}
