using Discord;
using Discord.Rest;
using Discord.WebSocket;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Primitives;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace DiscordService
{
	public class ChannelDetails
	{
		public ulong? VoiceId { get; set; }
		public ulong? TextId { get; set; }
		public string Name { get; set; }
		public string CategoryLevel { get; set; }
		public bool Voice { get; set; }
		public bool Text { get; set; }
		public bool Public { get; set; }
		public bool FemaleOnly { get; set; }
		public List<ulong> Members { get; set; }
	}

	internal static class CacheKeys
	{
		public static string AllChannels { get => "_Channels"; }
		public static string Channel(string name) => $"_Channel:{name.ToLower().Replace(' ', '-')}";
		public static string Eviction { get => "_EvictionSource"; }
	}

	// provides a singleton interface to Discord API with caching of data, to
	// avoid hitting the Discord API excessively
	public class DiscordApiService : IHostedService
	{
		private IMemoryCache _memoryCache;
		private IConfiguration _config;
		private ILogger _logger;

		private ulong _guildId;

		private DiscordSocketClient _discordClient;
		private RestGuild _guild;

		public async Task<DiscordSocketClient> Client()
		{
			if (_discordClient == null)
			{
				_discordClient = new DiscordSocketClient();

				
			}

			if (_discordClient.ConnectionState != ConnectionState.Connected)
			{
				await _discordClient.LoginAsync(TokenType.Bot, _config["Discord:BotToken"]);
				await _discordClient.StartAsync();

				if (ulong.TryParse(_config["Discord:GuildId"], out _guildId) == false)
				{
					throw new ArgumentNullException("GuildId");
				}

				_guild = await _discordClient.Rest.GetGuildAsync(_guildId);

				// set up a notification for new members joining the server
				_discordClient.UserJoined += (user) =>
				{
					if (string.IsNullOrWhiteSpace(user.Nickname))
						return SendMessage(849774281105866762, $"{user.Username} has joined Discord, but is not connected yet");

					return SendMessage(849774281105866762, $"{user.Nickname} ({user.Username}) has joined Discord, and is connected");
				};

				_discordClient.GuildMemberUpdated += (userBefore, userAfter) =>
				{
					if (string.IsNullOrWhiteSpace(userBefore.Nickname) && string.IsNullOrWhiteSpace(userAfter.Nickname) == false)
						return SendMessage(849774281105866762, $"{userAfter.Nickname} ({userAfter.Username}) has been fully connected");

					return Task.CompletedTask;
				};
			}

			return _discordClient;
		}

		public DiscordApiService(IMemoryCache memoryCache, IConfiguration config, ILogger<DiscordApiService> logger)
		{
			_memoryCache = memoryCache;
			_config = config;
			_logger = logger;
		}

		public async Task<IInviteMetadata> GetInitialInvite()
		{
			var client = await Client();

			if (!_memoryCache.TryGetValue<IEnumerable<RestGuildChannel>>(CacheKeys.AllChannels, out var channels))
			{
				channels = await _guild.GetTextChannelsAsync();
			}

			var general = channels.First(row => row.Name == "general") as RestTextChannel;

			var invite = await general.CreateInviteAsync(maxAge: (int)TimeSpan.FromDays(1).TotalSeconds, maxUses: 1, isTemporary: false, isUnique: true);

			return invite;
		}

		public async Task<IInviteMetadata> GetTemporaryInviteAsync()
		{
			var client = await Client();

			if (!_memoryCache.TryGetValue<IEnumerable<RestGuildChannel>>(CacheKeys.AllChannels, out var channels))
			{
				channels = await _guild.GetTextChannelsAsync();
			}

			var general = channels.First(row => row.Name == "general") as RestTextChannel;

			var invite = await general.CreateInviteAsync(maxAge: (int)TimeSpan.FromDays(1).TotalSeconds, maxUses: 3, isTemporary: true, isUnique: true);

			return invite;
		}

		private CancellationChangeToken EvictionChangeSource
		{
			get
			{
				if (!_memoryCache.TryGetValue<CancellationTokenSource>(CacheKeys.Eviction, out var tokenSource))
				{
					tokenSource = new CancellationTokenSource(TimeSpan.FromMinutes(10));

					_memoryCache.Set(CacheKeys.Eviction, tokenSource, new MemoryCacheEntryOptions
					{
						Priority = CacheItemPriority.NeverRemove
					});
				}

				return new CancellationChangeToken(tokenSource.Token);
			}
		}

		public void ClearCache()
		{
			if (_memoryCache.TryGetValue<CancellationTokenSource>(CacheKeys.Eviction, out var tokenSource))
			{
				tokenSource.Cancel();

				// the token has been canceled, so we'll need to generate a new one when required later
				_memoryCache.Remove(CacheKeys.Eviction);
			}
		}

		public async Task<ChannelDetails> GetChannelAsync(string name)
		{
			var cacheKeyName = CacheKeys.Channel(name);

			if (!_memoryCache.TryGetValue<ChannelDetails>(cacheKeyName, out var channelDetails))
			{
				var channels = await GetChannelsAsync();

				foreach (var channel in channels)
				{
					if (CacheKeys.Channel(channel.Name) == cacheKeyName)
					{
						_logger.LogInformation("Retrieved channel {0}", name);

						return channel;
					}
				}

				_logger.LogInformation("Unable to locate channel {0}", name);

				return null;
			}

			_logger.LogInformation("Retrieved channel {0} from cache", name);

			return channelDetails;
		}

		public async Task<IEnumerable<ChannelDetails>> GetChannelsAsync()
		{
			var client = await Client();

			if (!_memoryCache.TryGetValue<IEnumerable<RestGuildChannel>>(CacheKeys.AllChannels, out var allChannels))
			{
				allChannels = await _guild.GetChannelsAsync();

				_logger.LogInformation("Unable to fetch all channels from cache");

				_memoryCache.Set<IEnumerable<RestGuildChannel>>(CacheKeys.AllChannels, allChannels, EvictionChangeSource);
			}

			var parentChannelNames = new string[] { "A", "B", "C", "D" }
					.SelectMany(category => new string[]
					{
						  $"{category}-Grade Text",
						  $"{category}-Grade Voice",
						  $"{category}-Grade Hellcatz Text",
						  $"{category}-Grade Hellcatz Voice"
					});

			var parentChannels = allChannels.Where(row => parentChannelNames.Contains(row.Name));
			var femaleOnlyChannels = parentChannels.Where(row => row.Name.Contains("Hellcatz"));

			var nestedChannels = allChannels.Select(row => row as INestedChannel)
				.Where(row => row != null)
				.Where(row => parentChannels.Any(parent => parent.Id == row.CategoryId));

			var channels = new Dictionary<string, ChannelDetails>();

			foreach (var channel in nestedChannels)
			{
				string nameLookup = CacheKeys.Channel(channel.Name);

				if (!channels.TryGetValue(nameLookup, out var channelDetail))
				{
					channelDetail = new ChannelDetails
					{
						Name = channel.Name,
						CategoryLevel = parentChannels.Single(row => row.Id == channel.CategoryId).Name[0].ToString(),
						Text = false,
						Voice = false,
						Public = true,
						FemaleOnly = false,
						Members = new List<ulong>()
					};

					channels.Add(nameLookup, channelDetail);
				}

				if (channel is IVoiceChannel)
				{
					channelDetail.Name = channel.Name;
					channelDetail.Voice = true;
					channelDetail.VoiceId = channel.Id;

					var permissions = channel.GetPermissionOverwrite(_guild.EveryoneRole);

					channelDetail.Public = !permissions.HasValue || permissions.Value.Connect != PermValue.Deny;
				}

				if (channel is ITextChannel)
				{
					channelDetail.Text = true;
					channelDetail.TextId = channel.Id;

					var permissions = channel.GetPermissionOverwrite(_guild.EveryoneRole);

					// this should always be true, which we were previously using to gate populating channel members
					//-- if (permissions.HasValue && permissions.Value.ViewChannel == PermValue.Deny)

					var members = channel.PermissionOverwrites
						.Where(row => row.Permissions.ViewChannel == PermValue.Allow)
						.Where(row => row.TargetType == PermissionTarget.User)
						.Select(row => row.TargetId);

					channelDetail.Members.AddRange(members);
				}

				if (femaleOnlyChannels.Any(row => row.Id == channel.CategoryId))
				{
					channelDetail.FemaleOnly = true;
				}
			}

			StringBuilder builder = new StringBuilder();

			foreach (var channelDetail in channels)
			{
				_memoryCache.GetOrCreate(channelDetail.Key, entry =>
				{
					entry.AddExpirationToken(EvictionChangeSource);

					return channelDetail.Value;
				});

				builder.AppendLine($"Channel: {channelDetail.Value.Name}");
				builder.AppendLine($"  VoiceId: {channelDetail.Value.VoiceId}");
				builder.AppendLine($"  TextId: {channelDetail.Value.TextId}");
				builder.AppendLine($"  Members: {channelDetail.Value.Members.Count}");
			}

			_logger.LogInformation(builder.ToString());

			return channels.Values.ToList();
		}

		// returns a list of ids that cannot be found in the server
		public async Task<IEnumerable<ulong>> CreateChannel(ChannelDetails channel)
		{
			if (channel.Members == null)
			{
				throw new ArgumentNullException(nameof(channel.Members));
			}

			if (_memoryCache.TryGetValue<ChannelDetails>(CacheKeys.Channel(channel.Name), out var existingChannel))
			{
				throw new ArgumentException("Channel already exists");
			}

			var client = await Client();

			var denyChannelPermission = new OverwritePermissions(viewChannel: PermValue.Deny);
			var allowChannelPermission = new OverwritePermissions(viewChannel: PermValue.Allow);

			var denyVoiceChannelPermission = new OverwritePermissions(viewChannel: PermValue.Deny, connect: PermValue.Deny);
			var allowVoiceChannelPermission = new OverwritePermissions(viewChannel: PermValue.Allow, connect: PermValue.Allow);

			var categories = await _guild.GetCategoryChannelsAsync();

			var categoryName = channel.FemaleOnly ? $"{channel.CategoryLevel}-Grade Hellcatz" : $"{channel.CategoryLevel}-Grade";

			var textCategory = categories.SingleOrDefault(channel => channel.Name == $"{categoryName} Text");
			var voiceCategory = categories.SingleOrDefault(channel => channel.Name == $"{categoryName} Voice");

			if (!_memoryCache.TryGetValue<IEnumerable<RestGuildChannel>>(CacheKeys.AllChannels, out var allChannels))
			{
				allChannels = await _guild.GetChannelsAsync();

				_memoryCache.Set<IEnumerable<RestGuildChannel>>(CacheKeys.AllChannels, allChannels, EvictionChangeSource);
			}

			IEnumerable<INestedChannel> nestedChannels = allChannels.Select(row => row as INestedChannel)
				.Where(row => row != null)
				.Where(row => (channel.Text && row.CategoryId == textCategory.Id) || (channel.Voice && row.CategoryId == voiceCategory.Id));

			var discordUsers = await Task.WhenAll<RestGuildUser>(channel.Members.Select(snowflake => _guild.GetUserAsync(snowflake)));

			if (channel.Voice && nestedChannels.Any(row => row.CategoryId == voiceCategory.Id && row.Name.ToLower() == channel.Name.ToLower()) == false)
			{
				var voiceChannel = await _guild.CreateVoiceChannelAsync(channel.Name, props =>
				{
					props.CategoryId = voiceCategory.Id;
				});

				channel.VoiceId = voiceChannel.Id;

				if (channel.Public == false)
				{
					await voiceChannel.AddPermissionOverwriteAsync(_guild.EveryoneRole, denyVoiceChannelPermission);
				}

				foreach (var user in discordUsers)
				{
					if (user == null) continue;

					await voiceChannel.AddPermissionOverwriteAsync(user, allowVoiceChannelPermission);
				}

				nestedChannels = nestedChannels.Append(voiceChannel);
			}

			var textChannelName = channel.Name.ToLower().Replace(' ', '-');
			if (channel.Text && nestedChannels.Any(row => row.CategoryId == textCategory.Id && row.Name.ToLower() == textChannelName) == false)
			{
				var textChannel = await _guild.CreateTextChannelAsync(textChannelName, props =>
				{
					props.CategoryId = textCategory.Id;
				});

				channel.TextId = textChannel.Id;

				await textChannel.AddPermissionOverwriteAsync(_guild.EveryoneRole, denyChannelPermission);

				foreach (var snowflake in channel.Members)
				{
					var user = await _guild.GetUserAsync(snowflake);

					if (user == null) continue;

					await textChannel.AddPermissionOverwriteAsync(user, allowChannelPermission);
				}

				nestedChannels = nestedChannels.Append(textChannel);
			}

			var reorderedChannels = nestedChannels
				.OrderBy(row => row.Name)
				.Select((row, index) => new ReorderChannelProperties(row.Id, index));

			// sort our channels
			await _guild.ReorderChannelsAsync(reorderedChannels);

			_logger.LogInformation("Created channel {0}, invalidating all channels list as well", channel.Name);

			_memoryCache.Set<ChannelDetails>(CacheKeys.Channel(channel.Name), channel, EvictionChangeSource);

			// invalid our list of channels
			_memoryCache.Remove(CacheKeys.AllChannels);

			return channel.Members.Except(discordUsers.Where(row => row != null).Select(row => row.Id));
		}

		public async Task ModifyChannelMembers(ChannelDetails channel)
		{
			if (channel.Members == null)
			{
				throw new ArgumentNullException(nameof(channel.Members));
			}

			var client = await Client();

			var existingChannel = await GetChannelAsync(channel.Name);

			if (existingChannel == null)
			{
				throw new ArgumentNullException(nameof(channel.Name));
			}

			var addedMembers = await Task
					.WhenAll<RestGuildUser>(channel.Members.Except(existingChannel.Members)
						.Select(snowflake => _guild.GetUserAsync(snowflake)));
			var removedMembers = await Task
				.WhenAll<RestGuildUser>(existingChannel.Members.Where(snowflake => channel.Members.Contains(snowflake) == false)
					.Select(snowflake => _guild.GetUserAsync(snowflake)));

			existingChannel.Members = channel.Members;

			if (existingChannel.VoiceId.HasValue)
			{
				var voiceChannel = await _guild.GetChannelAsync(channel.VoiceId.Value);

				var denyVoiceChannelPermission = new OverwritePermissions(viewChannel: PermValue.Deny, connect: PermValue.Deny);
				var allowVoiceChannelPermission = new OverwritePermissions(viewChannel: PermValue.Allow, connect: PermValue.Allow);

				foreach (var added in addedMembers)
				{
					if (added == null) continue;

					await voiceChannel.AddPermissionOverwriteAsync(added, allowVoiceChannelPermission);
				}

				foreach (var removed in removedMembers)
				{
					if (removed == null) continue;

					await voiceChannel.RemovePermissionOverwriteAsync(removed);
				}
			}

			if (existingChannel.TextId.HasValue)
			{
				var textChannel = await _guild.GetChannelAsync(channel.TextId.Value);

				var denyChannelPermission = new OverwritePermissions(viewChannel: PermValue.Deny);
				var allowChannelPermission = new OverwritePermissions(viewChannel: PermValue.Allow);

				await textChannel.AddPermissionOverwriteAsync(_guild.EveryoneRole, denyChannelPermission);

				foreach (var added in addedMembers)
				{
					if (added == null) continue;

					await textChannel.AddPermissionOverwriteAsync(added, allowChannelPermission);
				}

				foreach (var removed in removedMembers)
				{
					if (removed == null) continue;

					await textChannel.RemovePermissionOverwriteAsync(removed);
				}
			}

			_logger.LogInformation("Updated channel membership for {0}", existingChannel.Name);

			_memoryCache.Remove(CacheKeys.Channel(existingChannel.Name));
			_memoryCache.Set<ChannelDetails>(CacheKeys.Channel(existingChannel.Name), existingChannel, EvictionChangeSource);
		}

		public async Task DeleteMember(long snowflake, string reason)
		{
			await Client();

			var user = await _guild.GetUserAsync((ulong)snowflake);

			await user.KickAsync(reason);
		}

		public async Task DeleteMembers(IEnumerable<long> snowflakes, string reason)
		{
			await Client();

			var tasks = new List<Task>();

			foreach (var snowflake in snowflakes)
			{
				var user = await _guild.GetUserAsync((ulong)snowflake);

				tasks.Add(user.KickAsync(reason));
			}

			Task.WaitAll(tasks.ToArray());
		}

		public async Task<IEnumerable<ulong>> GetMembers()
		{
			await Client();

			var snowflakes = new List<ulong>();

			await foreach (var users in _guild.GetUsersAsync())
			{
				snowflakes.AddRange(users.Select(user => user.Id));
			}

			return snowflakes;
		}

		public async Task SendMessage(ulong channelId, string message)
		{
			var client = await Client();

			var textChannel = client.GetGuild(_guildId).GetTextChannel(channelId);

			await textChannel.SendMessageAsync(text: message);
		}

		public Task StartAsync(CancellationToken cancellationToken)
		{
			return Client();
		}

		public Task StopAsync(CancellationToken cancellationToken)
		{
			return Task.CompletedTask;
		}
	}
}
