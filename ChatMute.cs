using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using Vintagestory.API.Common;
using Vintagestory.API.Config;
using Vintagestory.API.Datastructures;
using Vintagestory.API.Server;

namespace ChatMute
{
    public class ChatMuteModSystem : ModSystem
    {
        private ICoreServerAPI sapi;
        private ConcurrentDictionary<string, DateTime> mutedPlayers = new ConcurrentDictionary<string, DateTime>();
        private const string MUTE_DATA_KEY = "chatmute_data";

        public override bool ShouldLoad(EnumAppSide forSide)
        {
            return forSide == EnumAppSide.Server;
        }

        public override void StartServerSide(ICoreServerAPI api)
        {
            sapi = api;

			LoadMuteData();
			
            api.Event.PlayerChat += OnPlayerChat;


            api.ChatCommands.Create("mute")
                .WithDescription(Lang.Get("globalchatmute:mute-desc"))
                .WithArgs(
                    api.ChatCommands.Parsers.Word(Lang.Get("globalchatmute:mute-arg-player")),
                    api.ChatCommands.Parsers.Word(Lang.Get("globalchatmute:mute-arg-duration1")),
                    api.ChatCommands.Parsers.OptionalWord(Lang.Get("globalchatmute:mute-arg-duration2")),
                    api.ChatCommands.Parsers.OptionalWord(Lang.Get("globalchatmute:mute-arg-duration3"))
                )
                .RequiresPrivilege(Privilege.kick)
                .HandleWith(OnMuteCommand);

            api.ChatCommands.Create("unmute")
                .WithDescription(Lang.Get("globalchatmute:unmute-desc"))
                .WithArgs(api.ChatCommands.Parsers.Word(Lang.Get("globalchatmute:unmute-arg-player")))
                .RequiresPrivilege(Privilege.kick)
                .HandleWith(OnUnmuteCommand);

            api.ChatCommands.Create("mutelist")
                .WithDescription(Lang.Get("globalchatmute:mutelist-desc"))
                .RequiresPrivilege(Privilege.kick)
                .HandleWith(OnMuteListCommand);

            api.Event.Timer(CleanupExpiredMutes, 30000);
        }

        private void OnPlayerChat(IServerPlayer byPlayer, int channelId, ref string message, ref string data, BoolRef consumed)
        {
            if (channelId == GlobalConstants.GeneralChatGroup)
            {
                string playerUID = byPlayer.PlayerUID;
                if (mutedPlayers.TryGetValue(playerUID, out DateTime muteExpiry))
                {
                    if (DateTime.UtcNow < muteExpiry)
                    {
                        consumed.value = true;
                        TimeSpan remaining = muteExpiry - DateTime.UtcNow;
                        string remainingTime = FormatTimeRemaining(remaining);
                        byPlayer.SendMessage(GlobalConstants.GeneralChatGroup,
                            Lang.Get("globalchatmute:mute-notify", remainingTime),
                            EnumChatType.Notification);
                        return;
                    }
                    else
                    {
                        mutedPlayers.TryRemove(playerUID, out _);
                    }
                }
            }
        }

        private TextCommandResult OnMuteCommand(TextCommandCallingArgs args)
        {
            string targetPlayerName = (string)args.Parsers[0].GetValue();
            string duration1 = (string)args.Parsers[1].GetValue();
            string duration2 = (string)args.Parsers[2].GetValue();
            string duration3 = (string)args.Parsers[3].GetValue();

            // Collect all non-null duration arguments
            List<string> durations = new List<string> { duration1 };
            if (!string.IsNullOrWhiteSpace(duration2))
                durations.Add(duration2);
            if (!string.IsNullOrWhiteSpace(duration3))
                durations.Add(duration3);

            // Parse duration arguments in any order
            int totalMinutes = ParseDurations(durations);
            if (totalMinutes <= 0)
            {
                return TextCommandResult.Error(Lang.Get("globalchatmute:mute-error-duration"));
            }

            string targetPlayerUID = FindPlayerUID(targetPlayerName);
            if (targetPlayerUID == null)
            {
                return TextCommandResult.Error(Lang.Get("globalchatmute:mute-error-notfound", targetPlayerName));
            }

            DateTime muteExpiry = DateTime.UtcNow.AddMinutes(totalMinutes);
            mutedPlayers.AddOrUpdate(targetPlayerUID, muteExpiry, (key, oldValue) => muteExpiry);
            SaveMuteData();

            IServerPlayer targetPlayer = (IServerPlayer)sapi.World.AllOnlinePlayers
                .FirstOrDefault(p => p.PlayerUID == targetPlayerUID);

            if (targetPlayer != null)
            {
                targetPlayer.SendMessage(GlobalConstants.GeneralChatGroup,
                    Lang.Get("globalchatmute:mute-notify-duration", FormatDurationFriendly(totalMinutes)),
                    EnumChatType.Notification);
            }

            return TextCommandResult.Success(Lang.Get("globalchatmute:mute-success", targetPlayerName, FormatDurationFriendly(totalMinutes)));
        }

        private int ParseDurations(List<string> durationStrings)
        {
            int totalMinutes = 0;
            const int MAX_DAYS = 365;
            const int MAX_HOURS = 24;
            const int MAX_MINUTES = 60;

            foreach (string durationString in durationStrings)
            {
                if (string.IsNullOrWhiteSpace(durationString))
                    continue;

                string lower = durationString.ToLowerInvariant();
                
                // Extract number and unit from format like "1d", "20h", "45m"
                int i = 0;
                while (i < lower.Length && char.IsDigit(lower[i]))
                    i++;

                if (i == 0 || i >= lower.Length)
                    return -1; // No digits or no unit

                string numberPart = lower.Substring(0, i);
                char unit = lower[i];

                // There should be nothing after the unit
                if (i + 1 != lower.Length)
                    return -1; // Invalid format

                if (!int.TryParse(numberPart, out int value) || value <= 0)
                    return -1; // Invalid number

                switch (unit)
                {
                    case 'd':
                        if (value > MAX_DAYS)
                            return -1;
                        totalMinutes += value * 24 * 60;
                        break;
                    case 'h':
                        if (value > MAX_HOURS)
                            return -1;
                        totalMinutes += value * 60;
                        break;
                    case 'm':
                        if (value > MAX_MINUTES)
                            return -1;
                        totalMinutes += value;
                        break;
                    default:
                        return -1; // Invalid unit
                }
            }

            return totalMinutes;
        }

        private string FormatDurationFriendly(int totalMinutes)
        {
            int days = totalMinutes / (24 * 60);
            int hours = (totalMinutes % (24 * 60)) / 60;
            int minutes = totalMinutes % 60;

            if (days > 0)
                return Lang.Get("globalchatmute:duration-days", days, hours, minutes);
            if (hours > 0)
                return Lang.Get("globalchatmute:duration-hours", hours, minutes);
            return Lang.Get("globalchatmute:duration-minutes", minutes);
        }

        private TextCommandResult OnUnmuteCommand(TextCommandCallingArgs args)
        {
            string targetPlayerName = (string)args.Parsers[0].GetValue();
            string targetPlayerUID = FindPlayerUID(targetPlayerName);


            if (targetPlayerUID == null)
            {
                return TextCommandResult.Error(Lang.Get("globalchatmute:mute-error-notfound", targetPlayerName));
            }

            if (mutedPlayers.TryRemove(targetPlayerUID, out _))
            {
                IServerPlayer targetPlayer = (IServerPlayer)sapi.World.AllOnlinePlayers
                    .FirstOrDefault(p => p.PlayerUID == targetPlayerUID);

                if (targetPlayer != null)
                {
                    targetPlayer.SendMessage(GlobalConstants.GeneralChatGroup,
                        Lang.Get("globalchatmute:unmute-notify"),
                        EnumChatType.Notification);
                }

                return TextCommandResult.Success(Lang.Get("globalchatmute:unmute-success", targetPlayerName));
            }

            return TextCommandResult.Error(Lang.Get("globalchatmute:unmute-error-notmuted", targetPlayerName));
        }

        private TextCommandResult OnMuteListCommand(TextCommandCallingArgs args)
        {
            if (mutedPlayers.IsEmpty)
            {
                return TextCommandResult.Success(Lang.Get("globalchatmute:mutelist-empty"));
            }

            var activeMutes = mutedPlayers
                .Where(kvp => kvp.Value > DateTime.UtcNow)
                .ToList();

            if (!activeMutes.Any())
            {
                return TextCommandResult.Success(Lang.Get("globalchatmute:mutelist-empty"));
            }

            string result = Lang.Get("globalchatmute:mutelist-header", activeMutes.Count);
            foreach (var mute in activeMutes)
            {
                string playerName = GetPlayerNameByUID(mute.Key) ?? Lang.Get("globalchatmute:unknown-player");
                TimeSpan remaining = mute.Value - DateTime.UtcNow;
                string timeRemaining = FormatTimeRemaining(remaining);
                result += Lang.Get("globalchatmute:mutelist-entry", playerName, timeRemaining);
            }
            return TextCommandResult.Success(result.TrimEnd('\n'));
        }

        private void CleanupExpiredMutes()
        {
            var expiredMutes = mutedPlayers
                .Where(kvp => kvp.Value <= DateTime.UtcNow)
                .Select(kvp => kvp.Key)
                .ToList();

            bool dataChanged = false;
            foreach (string playerUID in expiredMutes)
            {
                if (mutedPlayers.TryRemove(playerUID, out _))
                {
                    dataChanged = true;
                }
            }

            if (dataChanged)
            {
                SaveMuteData();
            }
        }

        private void LoadMuteData()
        {
            try
            {
                byte[] data = sapi.WorldManager.SaveGame.GetData(MUTE_DATA_KEY);
                if (data != null)
                {
                    TreeAttribute tree = new TreeAttribute();
                    using (var ms = new System.IO.MemoryStream(data))
                    {
                        using (var reader = new System.IO.BinaryReader(ms))
                        {
                            tree.FromBytes(reader);
                        }
                    }

                    foreach (var entry in tree)
                    {
                        if (long.TryParse(entry.Value.GetValue().ToString(), out long ticks))
                        {
                            DateTime expiry = new DateTime(ticks, DateTimeKind.Utc);
                            if (expiry > DateTime.UtcNow)
                            {
                                mutedPlayers[entry.Key] = expiry;
                            }
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                sapi.World.Logger.Warning(Lang.Get("globalchatmute:mute-load-error", ex.Message));
            }
        }

        private void SaveMuteData()
        {
            try
            {
                TreeAttribute tree = new TreeAttribute();
                foreach (var mute in mutedPlayers)
                {
                    tree.SetLong(mute.Key, mute.Value.Ticks);
                }

                using (var ms = new System.IO.MemoryStream())
                {
                    using (var writer = new System.IO.BinaryWriter(ms))
                    {
                        tree.ToBytes(writer);
                    }
                    sapi.WorldManager.SaveGame.StoreData(MUTE_DATA_KEY, ms.ToArray());
                }
            }
            catch (Exception ex)
            {
                sapi.World.Logger.Error(Lang.Get("globalchatmute:mute-save-error", ex.Message));
            }
        }

        private string FindPlayerUID(string playerName)
        {
            var onlinePlayer = sapi.World.AllOnlinePlayers
                .FirstOrDefault(p => p.PlayerName.Equals(playerName, StringComparison.OrdinalIgnoreCase));

            if (onlinePlayer != null)
            {
                return onlinePlayer.PlayerUID;
            }

            try
            {
                var playerData = sapi.PlayerData.GetPlayerDataByLastKnownName(playerName);
                if (playerData != null)
                {
                    return playerData.PlayerUID;
                }
            }
            catch (Exception ex)
            {
                sapi.World.Logger.Warning(Lang.Get("globalchatmute:findplayer-error", playerName, ex.Message));
            }

            var offlinePlayer = sapi.World.AllPlayers
                .FirstOrDefault(p => p.PlayerName.Equals(playerName, StringComparison.OrdinalIgnoreCase));

            return offlinePlayer?.PlayerUID;
        }

        private string GetPlayerNameByUID(string playerUID)
        {
            var onlinePlayer = sapi.World.AllOnlinePlayers
                .FirstOrDefault(p => p.PlayerUID == playerUID);

            if (onlinePlayer != null)
            {
                return onlinePlayer.PlayerName;
            }

            try
            {
                var playerData = sapi.PlayerData.GetPlayerDataByUid(playerUID);
                return playerData?.LastKnownPlayername;
            }
            catch
            {
                return null;
            }
        }

        private string FormatTimeRemaining(TimeSpan timeSpan)
        {
            if (timeSpan.TotalDays >= 1)
            {
                return Lang.Get("globalchatmute:time-days", (int)timeSpan.TotalDays, timeSpan.Hours, timeSpan.Minutes);
            }
            if (timeSpan.TotalHours >= 1)
            {
                return Lang.Get("globalchatmute:time-hours", timeSpan.Hours, timeSpan.Minutes);
            }
            return Lang.Get("globalchatmute:time-minutes", timeSpan.Minutes, timeSpan.Seconds);
        }

        public override void Dispose()
        {
            if (sapi != null)
            {
				SaveMuteData();
                sapi.Event.PlayerChat -= OnPlayerChat;
            }
        }
    }
}