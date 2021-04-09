﻿using Discord;
using Discord.Rest;
using Discord.WebSocket;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using System;
using System.Collections.Generic;
using System.Linq;
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
	}

	// provides a singleton interface to Discord API with caching of data, to
	// avoid hitting the Discord API excessively
	public class DiscordService
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
			}

			return _discordClient;
		}

		public DiscordService(IMemoryCache memoryCache, IConfiguration config, ILogger<DiscordService> logger)
		{
			_memoryCache = memoryCache;
			_config = config;
			_logger = logger;
		}

		public async Task<IInviteMetadata> GetTemporaryInviteAsync()
		{
			var client = Client();

			if (!_memoryCache.TryGetValue<IEnumerable<RestGuildChannel>>(CacheKeys.AllChannels, out var channels))
			{
				channels = await _guild.GetTextChannelsAsync();
			}

			var general = channels.First(row => row.Name == "general") as RestTextChannel;

			var invite = await general.CreateInviteAsync(maxAge: (int)TimeSpan.FromDays(2).TotalSeconds, maxUses: 3, isTemporary: true, isUnique: true);

			return invite;
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
			var client = Client();

			if (!_memoryCache.TryGetValue<IEnumerable<RestGuildChannel>>(CacheKeys.AllChannels, out var allChannels))
			{
				allChannels = await _guild.GetChannelsAsync();

				_logger.LogInformation("Unable to fetch all channels from cache");

				_memoryCache.Set<IEnumerable<RestGuildChannel>>(CacheKeys.AllChannels, allChannels, TimeSpan.FromMinutes(10));
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

			foreach (var channelDetail in channels)
			{
				_memoryCache.Set<ChannelDetails>(CacheKeys.Channel(channelDetail.Key), channelDetail.Value, TimeSpan.FromMinutes(10));
			}

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

			var client = Client();

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

				_memoryCache.Set<IEnumerable<RestGuildChannel>>(CacheKeys.AllChannels, allChannels);
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

			_memoryCache.Set<ChannelDetails>(CacheKeys.Channel(channel.Name), channel, TimeSpan.FromMinutes(10));

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

			var client = Client();

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

			_memoryCache.Set<ChannelDetails>(CacheKeys.Channel(existingChannel.Name), existingChannel, TimeSpan.FromMinutes(10));
		}
	}
}
