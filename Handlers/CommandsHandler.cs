using Discord;
using Discord.Commands;
using Discord.WebSocket;
using Microsoft.Extensions.DependencyInjection;
using System.Reflection;
using CharacterAI_Discord_Bot.Service;
using CharacterAI_Discord_Bot.Models;
using CharacterAI;
using static System.Net.Mime.MediaTypeNames;

namespace CharacterAI_Discord_Bot.Handlers
{
    public class CommandsHandler : HandlerService
    {
        internal Integration CurrentIntegration { get; }
        internal List<ulong> BlackList { get; set; } = new();
        internal List<Models.Channel> Channels { get; set; } = new();
        internal LastSearchQuery? LastSearch { get; set; }
        internal Dictionary<ulong, int> HuntedUsers { get; set; } = new(); // user id : reply chance

        private readonly Dictionary<ulong, int[]> _userMsgCount = new();
        private readonly DiscordSocketClient _client;
        private readonly IServiceProvider _services;
        private readonly CommandService _commands;
        private static readonly Config _config = GetConfig()!;
        
        public CommandsHandler(IServiceProvider services)
        {
            CurrentIntegration = new(BotConfig.UserToken);

            _services = services;
            _commands = services.GetRequiredService<CommandService>();
            _client = services.GetRequiredService<DiscordSocketClient>();

            _client.MessageReceived += HandleMessage;
            _client.ReactionAdded += HandleReaction;
            _client.ReactionRemoved += HandleReaction;
            _client.ButtonExecuted += HandleButton;
            _client.JoinedGuild += (s) => Task.Run(() =>
            {
                if (CurrentIntegration.CurrentCharacter.Name is string name)
                    _ = CurrentClientService.SetBotNickname(name, _client);
            });
        }

        private async Task HandleMessage(SocketMessage rawMsg)
        {
            var authorId = rawMsg.Author.Id;
            if (rawMsg is not SocketUserMessage message || authorId == _client.CurrentUser.Id)
                return;

            int argPos = 0;
            var random = new Random();
            string[] prefixes = BotConfig.BotPrefixes;
            var context = new SocketCommandContext(_client, message);

            bool isPrivate = context.Channel.Name.StartsWith("private");
            bool isDM = context.Guild is null;

            var cI = CurrentIntegration;
            var currentChannel = Channels.Find(c => c.Id == context.Channel.Id);

            bool hasMention = isDM || message.HasMentionPrefix(_client.CurrentUser, ref argPos);
            bool hasPrefix = hasMention || prefixes.Any(p => message.HasStringPrefix(p, ref argPos));
            bool hasReply = hasPrefix || (message.ReferencedMessage != null && message.ReferencedMessage.Author.Id == _client.CurrentUser.Id);
            bool randomReply = hasReply || (currentChannel is Channel cc && cc.Data.ReplyChance > (random.Next(99) + 0.001 + random.NextDouble())); // min: 0 + 0.001 + 0 = 0.001; max: 98 + 0.001 + 1 = 99.001
            // Any condition above or if user is hunted
            bool gottaReply = randomReply || (HuntedUsers.ContainsKey(authorId) && HuntedUsers[authorId] >= random.Next(100) + 1);

            if (!gottaReply) return;

            if (currentChannel is null)
            {
                if (isPrivate) return; // don't handle deactivated "private" chats

                var data = new CharacterDialogData(null, null);
                currentChannel = new Channel(context.Channel.Id, context.User.Id, data);
                Channels.Add(currentChannel);

                if (!cI.CurrentCharacter.IsEmpty)
                {
                    var historyId = await cI.CreateNewChatAsync() ?? cI.Chats[0];

                    currentChannel.Data.CharacterId = cI.CurrentCharacter.Id;
                    currentChannel.Data.HistoryId = historyId;
                }

                SaveData(channels: Channels);
            }

            // Update messages-per-minute counter.
            // If user has exceeded rate limit, or if message is a DM and these are disabled - return
            if ((isDM && !BotConfig.DMenabled) || UserIsBanned(context)) return;

            // Try to execute command
            var cmdResponse = await _commands.ExecuteAsync(context, argPos, _services);
            // If command was found and executed, return
            if (cmdResponse.IsSuccess) return;
            // If command was found but failed to execute, return
            if (cmdResponse.ErrorReason != "Unknown command.")
            {
                string text = $"{WARN_SIGN_DISCORD} Failed to execute command: {cmdResponse.ErrorReason} ({cmdResponse.Error})";
                if (isDM) text = "*Note: some commands are not intended to be called from DMs*\n" + text;

                await message.ReplyAsync(text).ConfigureAwait(false);
                return;
            }

            // If command was not found, perform character call
            if (cI.CurrentCharacter.IsEmpty)
            {
                await message.ReplyAsync($"{WARN_SIGN_DISCORD} Set a character first").ConfigureAwait(false);
                return;
            }

            if (message.Author.IsBot && currentChannel.Data.SkipNextBotMessage)
            {
                currentChannel.Data.SkipNextBotMessage = false;
                return;
            }

            if (currentChannel.Data.SkipMessages > 0)
                currentChannel.Data.SkipMessages--;
            else
                using (message.Channel.EnterTypingState())
                    _ = TryToCallCharacterAsync(context, currentChannel, isDM || isPrivate);
        }

        private async Task HandleReaction(Cacheable<IUserMessage, ulong> rawMessage, Cacheable<IMessageChannel, ulong> channel, SocketReaction reaction)
        {
            var user = reaction?.User.Value;
            if (user is null) return;

            var socketUser = (SocketUser)user;
            if (socketUser.IsBot) return;

            var message = await rawMessage.DownloadAsync();
            var currentChannel = Channels.Find(c => c.Id == message.Channel.Id);
            if (currentChannel is null) return;

            if (reaction!.Emote.Name == STOP_BTN.Name)
            {
                currentChannel.Data.SkipNextBotMessage = true;
                return;
            }

            bool userIsLastMessageAuthor = message.ReferencedMessage is IUserMessage um && socketUser.Id == um.Author.Id;
            bool msgIsSwipable = currentChannel.Data.LastCall is not null && rawMessage.Id == currentChannel.Data.LastCharacterCallMsgId;
            if (!userIsLastMessageAuthor || !msgIsSwipable) return;

            if (reaction.Emote.Name == ARROW_LEFT.Name && currentChannel.Data.LastCall!.CurrentReplyIndex > 0)
            {   // left arrow
                currentChannel.Data.LastCall!.CurrentReplyIndex--;
                _ = SwipeMessageAsync(message, currentChannel);

                return;
            }
            if (reaction.Emote.Name == ARROW_RIGHT.Name)
            {   // right arrow
                currentChannel.Data.LastCall!.CurrentReplyIndex++;
                _ = SwipeMessageAsync(message, currentChannel);

                return;
            }
        }

        // Navigate in search modal
        private async Task HandleButton(SocketMessageComponent component)
        {
            if (LastSearch is null) return;

            var context = new SocketCommandContext(_client, component.Message);
            var refMessage = await context.Message.Channel.GetMessageAsync(context.Message.Reference!.MessageId.Value);
            bool notAuthor = component.User.Id != refMessage.Author.Id;
            bool noPages = LastSearch!.Response.IsEmpty;
            if (notAuthor || UserIsBanned(context, checkOnly: true) || noPages) return;

            int tail = LastSearch!.Response!.Characters!.Count - (LastSearch.CurrentPage - 1) * 10;
            int maxRow = tail > 10 ? 10 : tail;

            switch (component.Data.CustomId)
            { // looks like shit...
                case "up":
                    if (LastSearch.CurrentRow == 1)
                        LastSearch.CurrentRow = maxRow;
                    else
                        LastSearch.CurrentRow--;
                    break;
                case "down":
                    if (LastSearch.CurrentRow > maxRow)
                        LastSearch.CurrentRow = 1;
                    else
                        LastSearch.CurrentRow++;
                    break;
                case "left":
                    LastSearch.CurrentRow = 1;

                    if (LastSearch.CurrentPage == 1)
                        LastSearch.CurrentPage = LastSearch.Pages;
                    else
                        LastSearch.CurrentPage--;
                    break;
                case "right":
                    LastSearch.CurrentRow = 1;

                    if (LastSearch.CurrentPage == LastSearch.Pages)
                        LastSearch.CurrentPage = 1;
                    else
                        LastSearch.CurrentPage++;
                    break;
                case "select":
                    var refContext = new SocketCommandContext(_client, (SocketUserMessage)refMessage);

                    using (refContext.Message.Channel.EnterTypingState())
                    {
                        int index = (LastSearch.CurrentPage - 1) * 10 + LastSearch.CurrentRow - 1;
                        var characterId = LastSearch.Response!.Characters![index].Id;
                        var character = await CurrentIntegration.GetInfoAsync(characterId);
                        if (character.IsEmpty) return;

                        _ = CommandsService.SetCharacterAsync(characterId!, this, refContext);

                        var imageUrl = TryGetImage(character.AvatarUrlFull!).Result ?
                            character.AvatarUrlFull : TryGetImage(character.AvatarUrlMini!).Result ?
                            character.AvatarUrlMini : null;

                        var embed = new EmbedBuilder()
                        {
                            ImageUrl = imageUrl,
                            Title = $"✅ Selected - {character.Name}",
                            Footer = new EmbedFooterBuilder().WithText($"Created by {character.Author}"),
                            Description = $"{character.Description}\n\n" +
                                          $"*Original link: [Chat with {character.Name}](https://beta.character.ai/chat?char={character.Id})*\n" +
                                          $"*Can generate images: {(character.ImageGenEnabled is true ? "Yes" : "No")}*\n" +
                                          $"*Interactions: {character.Interactions}*"
                        };

                        await component.UpdateAsync(c =>
                        {
                            c.Embed = embed.Build();
                            c.Components = null;
                        }).ConfigureAwait(false);
                    }
                    return;
                default:
                    return;
            }

            // Only if left/right/up/down is selected
            await component.UpdateAsync(c => c.Embed = BuildCharactersList(LastSearch))
                           .ConfigureAwait(false);
        }

        // Swipes
        private async Task SwipeMessageAsync(IUserMessage message, Models.Channel currentChannel)
        {
            if (currentChannel.Data.LastCall!.RepliesList.Count < currentChannel.Data.LastCall.CurrentReplyIndex + 1)
            {
                _ = message.ModifyAsync(msg => { msg.Content = $"( 🕓 Wait... )"; msg.AllowedMentions = AllowedMentions.None; });
                var historyId = currentChannel.Data.HistoryId;
                var parentMsgId = currentChannel.Data.LastCall.OriginalResponse.LastUserMsgId;
                var response = await CurrentIntegration.CallCharacterAsync(parentMsgId: parentMsgId, historyId: historyId);

                if (!response.IsSuccessful)
                {
                    _ = message.ModifyAsync(msg => { msg.Content = response.ErrorReason!.Replace(WARN_SIGN_UNICODE, WARN_SIGN_DISCORD); });
                    return;
                }
                currentChannel.Data.LastCall.RepliesList.AddRange(response.Replies);
            }
            var newReply = currentChannel.Data.LastCall.RepliesList[currentChannel.Data.LastCall.CurrentReplyIndex];
            currentChannel.Data.LastCall.CurrentPrimaryMsgId = newReply.Id;

            Embed? embed = null;
            if (newReply.HasImage && await TryGetImage(newReply.ImageRelPath!))
                embed = new EmbedBuilder().WithImageUrl(newReply.ImageRelPath).Build();

            _ = message.ModifyAsync(msg => { msg.Content = $"{newReply.Text}"; msg.Embed = embed; })
                .ConfigureAwait(false);
        }

        private async Task TryToCallCharacterAsync(SocketCommandContext context, Models.Channel currentChannel, bool inDMorPrivate)
        {
            // Get last call and remove buttons from it
            if (currentChannel.Data.LastCharacterCallMsgId != 0)
            {
                var lastMessage = await context.Message.Channel.GetMessageAsync(currentChannel.Data.LastCharacterCallMsgId);
                _ = RemoveButtonsAsync(lastMessage, context.Client.CurrentUser);
            }

            // Prepare text data
            string text = RemoveMention(context.Message.Content);
            
            int amode = currentChannel.Data.AudienceMode;
            
            string unformattedText = amode == 2 ? _config.MessageFormat.Replace("{reply}", _config.AudienceModeReplyFormat) : _config.MessageFormat;
            unformattedText = amode == 1 ? unformattedText.Replace("{username}", _config.AudienceModeNameFormat) : unformattedText;
            unformattedText = amode == 3 ? unformattedText.Replace("{username}", _config.AudienceModeNameFormat).Replace("{reply}", _config.AudienceModeReplyFormat) : _config.MessageFormat.Replace("{reply}", "").Replace("{username}", "");
            if (amode == 1 || amode == 3)
                unformattedText = AddUsername(unformattedText, context);
            if (amode == 2 || amode == 3)
                unformattedText = AddQuote(unformattedText, context.Message);
            text = unformattedText.Replace("{message}", text)
            
            // Prepare image data
            string? imgPath = null;
            //var attachments = context.Message.Attachments;
            //if (attachments.Any())
            //{   // Downloads first image from attachments and uploads it to server
            //    var file = attachments.First();
            //    string url = file.Url;
            //    string fileName = file.Filename; 
            //    var image = await TryDownloadImgAsync(url);

            //    bool isDownloaded = image is not null;
            //    string? path = isDownloaded ? await CurrentIntegration.UploadImageAsync(image!, fileName) : null;

            //    if (path is not null)
            //        imgPath = $"https://characterai.io/i/400/static/user/{path}";
            //}
            
            string historyId = currentChannel.Data.HistoryId!;
            ulong? primaryMsgId = currentChannel.Data.LastCall?.CurrentPrimaryMsgId;

            // Send message to a character
            var response = await CurrentIntegration.CallCharacterAsync(text, imgPath, historyId, primaryMsgId);
            currentChannel.Data.LastCall = new(response);

            if (response.IsSuccessful)
            {
                // Take first character answer by default and reply with it
                var reply = currentChannel.Data.LastCall!.RepliesList.First();
                _ = Task.Run(async () =>
                {
                    var msgId = await RespondOnMessage(context.Message, reply, inDMorPrivate, currentChannel.Data.ReplyDelay);
                    currentChannel.Data.LastCharacterCallMsgId = msgId;
                });
            }
            else // Alert with error message if call fails
                await context.Message.ReplyAsync(response.ErrorReason!.Replace(WARN_SIGN_UNICODE, WARN_SIGN_DISCORD)).ConfigureAwait(false);
        }

        private bool UserIsBanned(SocketCommandContext context, bool checkOnly = false)
        {
            ulong currUserId = context.Message.Author.Id;
            if (context.Guild is not null && currUserId == context.Guild.OwnerId)
                return false;

            if (BlackList.Contains(currUserId)) return true;
            if (checkOnly) return false;

            int currMinute = context.Message.CreatedAt.Minute + context.Message.CreatedAt.Hour * 60;

            // Start watching for user
            if (!_userMsgCount.ContainsKey(currUserId))
                _userMsgCount.Add(currUserId, new int[] { -1, 0 }); // current minute : count

            // Drop + update user stats if he replies in new minute
            if (_userMsgCount[currUserId][0] != currMinute)
            {
                _userMsgCount[currUserId][0] = currMinute;
                _userMsgCount[currUserId][1] = 0;
            }

            // Update messages count withing current minute
            _userMsgCount[currUserId][1]++;

            if (_userMsgCount[currUserId][1] == BotConfig.RateLimit - 1)
                context.Message.ReplyAsync($"{WARN_SIGN_DISCORD} Warning! If you proceed to call {context.Client.CurrentUser.Mention} " +
                                            "so fast, you'll be blocked from using it.");
            else if (_userMsgCount[currUserId][1] > BotConfig.RateLimit)
            {
                BlackList.Add(currUserId);
                _userMsgCount.Remove(currUserId);

                return true;
            }

            return false;
        }

        public async Task InitializeAsync()
            => await _commands.AddModulesAsync(Assembly.GetEntryAssembly(), _services);
    }
}
