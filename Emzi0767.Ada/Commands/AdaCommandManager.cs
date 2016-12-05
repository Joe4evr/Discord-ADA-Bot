﻿using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Text;
using System.Threading.Tasks;
using Discord;
using Discord.Commands;
using Discord.WebSocket;
using Emzi0767.Ada.Attributes;
using Emzi0767.Ada.Commands.Permissions;

namespace Emzi0767.Ada.Commands
{
    /// <summary>
    /// Handles all commands.
    /// </summary>
    public class AdaCommandManager
    {
        private Dictionary<string, AdaCommand> RegisteredCommands { get; set; }
        private Dictionary<string, IAdaPermissionChecker> RegisteredCheckers { get; set; }
        public int CommandCount { get { return this.GetCommands().Count(); } }
        public int CheckerCount { get { return this.RegisteredCheckers.Count; } }
        public string Prefix { get; private set; }

        /// <summary>
        /// Initializes the command handler.
        /// </summary>
        internal void Initialize()
        {
            L.W("ADA CMD", "Initializing commands");
            this.RegisterCheckers();
            this.RegisterCommands();
            this.InitCommands();
            L.W("ADA CMD", "Initialized");
        }

        /// <summary>
        /// Gets all registered commands.
        /// </summary>
        /// <returns>All registered commands.</returns>
        public IEnumerable<AdaCommand> GetCommands()
        {
            foreach (var cmd in this.RegisteredCommands.GroupBy(xkvp => xkvp.Value))
                yield return cmd.Key;
        }

        internal AdaCommand GetCommand(string name)
        {
            if (this.RegisteredCommands.ContainsKey(name))
                return this.RegisteredCommands[name];
            return null;
        }

        private void RegisterCheckers()
        {
            L.W("ADA CMD", "Registering permission checkers");
            this.RegisteredCheckers = new Dictionary<string, IAdaPermissionChecker>();
            var @as = AdaBotCore.PluginManager.PluginAssemblies;
            var ts = @as.SelectMany(xa => xa.DefinedTypes);
            var ct = typeof(IAdaPermissionChecker);
            foreach (var t in ts)
            {
                if (!ct.IsAssignableFrom(t.AsType()) || !t.IsClass || t.IsAbstract)
                    continue;

                var ipc = (IAdaPermissionChecker)Activator.CreateInstance(t.AsType());
                this.RegisteredCheckers.Add(ipc.Id, ipc);
                L.W("ADA CMD", "Registered checker '{0}' for type {1}", ipc.Id, t.ToString());
            }
            L.W("ADA CMD", "Registered {0:#,##0} checkers", this.RegisteredCheckers.Count);
        }

        private void RegisterCommands()
        {
            L.W("ADA CMD", "Registering commands");
            this.RegisteredCommands = new Dictionary<string, AdaCommand>();
            var @as = AdaBotCore.PluginManager.PluginAssemblies;
            var ts = @as.SelectMany(xa => xa.DefinedTypes);
            var ht = typeof(IAdaCommandModule);
            var ct = typeof(AdaCommandAttribute);
            var pt = typeof(AdaCommandParameterAttribute);
            foreach (var t in ts)
            {
                if (!ht.IsAssignableFrom(t.AsType()) || !t.IsClass || t.IsAbstract)
                    continue;
                
                var ch = (IAdaCommandModule)Activator.CreateInstance(t.AsType());
                L.W("ADA CMD", "Found module handler '{0}' in type {1}", ch.Name, t.ToString());
                foreach (var m in t.GetMethods(BindingFlags.Instance | BindingFlags.Public))
                {
                    var xct = m.GetCustomAttribute<AdaCommandAttribute>();
                    if (xct == null)
                        continue;

                    var xps = m.GetCustomAttributes<AdaCommandParameterAttribute>().ToArray();
                    var ats = new List<AdaCommandParameter>();
                    foreach (var xp in xps)
                        ats.Add(new AdaCommandParameter(xp.Order, xp.Name, xp.Description, xp.IsRequired, xp.IsCatchAll));

                    var aliases = xct.Aliases != null ? xct.Aliases.Split(';') : new string[] { };
                    var cmd = new AdaCommand(xct.Name, aliases, xct.Description, xct.CheckPermissions && this.RegisteredCheckers.ContainsKey(xct.CheckerId) ? this.RegisteredCheckers[xct.CheckerId] : null, m, ch, xct.RequiredPermission, ats);
                    var names = new string[1 + aliases.Length];
                    names[0] = cmd.Name;
                    if (aliases.Length > 0)
                        Array.Copy(aliases, 0, names, 1, aliases.Length);
                    if (!this.RegisteredCommands.ContainsKey(cmd.Name))
                    {
                        foreach (var name in names)
                        {
                            if (!this.RegisteredCommands.ContainsKey(name))
                                this.RegisteredCommands.Add(name, cmd);
                            else
                                L.W("ADA CMD", "Alias '{0}' for command '{1}' already taken, skipping", name, cmd.Name);
                        }
                        L.W("ADA CMD", "Registered command '{0}' for handler '{1}'", cmd.Name, ch.Name);
                    }
                    else
                        L.W("ADA CMD", "Command name '{0}' is already registered, skipping", cmd.Name);
                }
                L.W("ADA CMD", "Registered command module '{0}' for type {1}", ch.Name, t.ToString());
            }
            L.W("ADA CMD", "Registered {0:#,##0} commands", this.RegisteredCommands.GroupBy(xkvp => xkvp.Value).Count());
        }

        private void InitCommands()
        {
            L.W("ADA CMD", "Registering command handler");
            AdaBotCore.AdaClient.DiscordClient.MessageReceived += HandleCommand;
            L.W("ADA CMD", "Done");
        }

        private async Task HandleCommand(SocketMessage arg)
        {
            var msg = arg as SocketUserMessage;
            if (msg == null)
                return;

            var client = AdaBotCore.AdaClient.DiscordClient;
            var argpos = 0;
            var cprefix = '/';
            if (client.CurrentUser.Id != 207900508562653186u)
                cprefix = '?';
            this.Prefix = cprefix.ToString();

            if (msg.HasCharPrefix(cprefix, ref argpos) || msg.HasMentionPrefix(client.CurrentUser, ref argpos))
            {
                var cmdn = msg.Content.Substring(argpos);
                var argi = cmdn.IndexOf(' ');
                if (argi == -1)
                    argi = cmdn.Length;
                var args = cmdn.Substring(argi).Trim();
                cmdn = cmdn.Substring(0, argi);
                var cmd = this.GetCommand(cmdn);
                if (cmd == null)
                    return;

                var ctx = new AdaCommandContext();
                ctx.Message = msg;
                ctx.Command = cmd;
                ctx.RawArguments = this.ParseArgumentList(args);
                try
                {
                    await cmd.Execute(ctx);
                    this.CommandExecuted(ctx);
                }
                catch (Exception ex)
                {
                    this.CommandError(new AdaCommandErrorContext { Context = ctx, Exception = ex });
                }
            }
        }

        private void CommandError(AdaCommandErrorContext ctxe)
        {
            var ctx = ctxe.Context;
            L.W("DSC CMD", "User '{0}#{1}' failed to execute command '{2}' in guild '{3}' ({4}); reason: {5} ({6})", ctx.User.Username, ctx.User.Discriminator, ctx.Command != null ? ctx.Command.Name : "<unknown>", ctx.Guild.Name, ctx.Guild.IconId, ctxe.Exception != null ? ctxe.Exception.GetType().ToString() : "<unknown exception type>", ctxe.Exception != null ? ctxe.Exception.Message : "N/A");
            if (ctxe.Exception != null)
                L.X("DSC CMD", ctxe.Exception);
            
            var embed = new EmbedBuilder();
            embed.Title = "Error executing command";
            embed.Description = string.Format("User {0} failed to execute command **{1}**.", ctx.User.Mention, ctx.Command != null ? ctx.Command.Name : "<unknown>");
            embed.Author = new EmbedAuthorBuilder();
            embed.Author.IconUrl = AdaBotCore.AdaClient.DiscordClient.CurrentUser.AvatarUrl;
            embed.Author.Name = "ADA, a bot by Emzi0767";
            embed.Color = new Color(255, 127, 0);

            embed.AddField(x =>
            {
                x.IsInline = false;
                x.Name = "Reason";
                x.Value = ctxe.Exception != null ? ctxe.Exception.Message : "<unknown>";
            });

            if (ctxe.Exception != null)
            {
                embed.AddField(x =>
                {
                    x.IsInline = false;
                    x.Name = "Exception details";
                    x.Value = string.Format("**{0}**: {1}", ctxe.Exception.GetType().ToString(), ctxe.Exception.Message);
                });
            }

            ctx.Channel.SendMessageAsync("", false, embed).Wait();
        }

        private void CommandExecuted(AdaCommandContext ctx)
        {
            L.W("DSC CMD", "User '{0}#{1}' executed command '{2}' on server '{3}' ({4})", ctx.User.Username, ctx.User.Discriminator, ctx.Command.Name, ctx.Guild.Name, ctx.Guild.Id);
        }

        private IReadOnlyList<string> ParseArgumentList(string argstring)
        {
            if (string.IsNullOrWhiteSpace(argstring))
                return new List<string>().AsReadOnly();

            var arglist = new List<string>();
            var argsraw = argstring.Split(' ');
            var sb = new StringBuilder();
            var building_arg = false;
            foreach (var argraw in argsraw)
            {
                if (!building_arg && !argraw.StartsWith("\""))
                    arglist.Add(argraw);
                else if (!building_arg && argraw.StartsWith("\"") && argraw.EndsWith("\""))
                    arglist.Add(argraw.Substring(1, argraw.Length - 2));
                else if (!building_arg && argraw.StartsWith("\"") && !argraw.EndsWith("\""))
                {
                    building_arg = true;
                    sb.Append(argraw.Substring(1)).Append(' ');
                }
                else if (building_arg && !argraw.EndsWith("\""))
                    sb.Append(argraw).Append(' ');
                else if (building_arg && argraw.EndsWith("\"") && !argraw.EndsWith("\\\""))
                {
                    sb.Append(argraw.Substring(0, argraw.Length - 1));
                    arglist.Add(sb.ToString());
                    building_arg = false;
                    sb = new StringBuilder();
                }
                else if (building_arg && argraw.EndsWith("\\\""))
                    sb.Append(argraw.Remove(argraw.Length - 2, 1)).Append(' ');
            }

            return arglist.AsReadOnly();
        }
    }
}
