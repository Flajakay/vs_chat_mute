using System;
using System.Collections.Concurrent;
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
                .WithDescription("Временно заглушить игрока в глобальном чате")
                .WithArgs(api.ChatCommands.Parsers.Word("имя_игрока"), api.ChatCommands.Parsers.Int("минуты"))
                .RequiresPrivilege(Privilege.ban)
                .HandleWith(OnMuteCommand);

            api.ChatCommands.Create("unmute")
                .WithDescription("Убрать заглушение с игрока")
                .WithArgs(api.ChatCommands.Parsers.Word("имя_игрока"))
                .RequiresPrivilege(Privilege.ban)
                .HandleWith(OnUnmuteCommand);

            api.ChatCommands.Create("mutelist")
                .WithDescription("Показать всех заглушенных игроков")
                .RequiresPrivilege(Privilege.ban)
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
                            $"Вам запрещено писать в глобальный чат еще {remainingTime}", 
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
            int minutes = (int)args.Parsers[1].GetValue();

            if (minutes <= 0)
            {
                return TextCommandResult.Error("Количество минут должно быть больше 0");
            }

            string targetPlayerUID = FindPlayerUID(targetPlayerName);
            if (targetPlayerUID == null)
            {
                return TextCommandResult.Error($"Игрок '{targetPlayerName}' не найден");
            }

            DateTime muteExpiry = DateTime.UtcNow.AddMinutes(minutes);
            mutedPlayers.AddOrUpdate(targetPlayerUID, muteExpiry, (key, oldValue) => muteExpiry);

            IServerPlayer targetPlayer = (IServerPlayer)sapi.World.AllOnlinePlayers
                .FirstOrDefault(p => p.PlayerUID == targetPlayerUID);

            if (targetPlayer != null)
            {
                targetPlayer.SendMessage(GlobalConstants.GeneralChatGroup,
                    $"Вам запрещено писать в глобальном чате на {minutes} минут",
                    EnumChatType.Notification);
            }

            return TextCommandResult.Success($"Игрок '{targetPlayerName}' был заглушен в глобальном чате на {minutes} минут");
        }

        private TextCommandResult OnUnmuteCommand(TextCommandCallingArgs args)
        {
            string targetPlayerName = (string)args.Parsers[0].GetValue();
            string targetPlayerUID = FindPlayerUID(targetPlayerName);

            if (targetPlayerUID == null)
            {
                return TextCommandResult.Error($"Игрок '{targetPlayerName}' не найден");
            }

            if (mutedPlayers.TryRemove(targetPlayerUID, out _))
            {
                IServerPlayer targetPlayer = (IServerPlayer)sapi.World.AllOnlinePlayers
                    .FirstOrDefault(p => p.PlayerUID == targetPlayerUID);

                if (targetPlayer != null)
                {
                    targetPlayer.SendMessage(GlobalConstants.GeneralChatGroup,
                        "Вы снова можете писать в глобальном чате",
                        EnumChatType.Notification);
                }

                return TextCommandResult.Success($"С игрока '{targetPlayerName}' снято заглушение");
            }

            return TextCommandResult.Error($"Игрок '{targetPlayerName}' не заглушен");
        }

        private TextCommandResult OnMuteListCommand(TextCommandCallingArgs args)
        {
            if (mutedPlayers.IsEmpty)
            {
                return TextCommandResult.Success("В данный момент никто не заглушен");
            }

            var activeMutes = mutedPlayers
                .Where(kvp => kvp.Value > DateTime.UtcNow)
                .ToList();

            if (!activeMutes.Any())
            {
                return TextCommandResult.Success("В данный момент никто не заглушен");
            }

            string result = $"Заглушенные игроки ({activeMutes.Count}):\n";
            foreach (var mute in activeMutes)
            {
                string playerName = GetPlayerNameByUID(mute.Key) ?? "Неизвестен";
                TimeSpan remaining = mute.Value - DateTime.UtcNow;
                string timeRemaining = FormatTimeRemaining(remaining);
                result += $"• {playerName}: осталось {timeRemaining}\n";
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
                sapi.World.Logger.Warning($"[ChatMute] Не удалось загрузить данные заглушений: {ex.Message}");
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
                sapi.World.Logger.Error($"[ChatMute] Не удалось сохранить данные заглушений: {ex.Message}");
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
                sapi.World.Logger.Warning($"Не удалось получить данные игрока по имени '{playerName}': {ex.Message}");
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
                return $"{(int)timeSpan.TotalDays}д {timeSpan.Hours}ч {timeSpan.Minutes}м";
            }
            if (timeSpan.TotalHours >= 1)
            {
                return $"{timeSpan.Hours}ч {timeSpan.Minutes}м";
            }
            return $"{timeSpan.Minutes}м {timeSpan.Seconds}с";
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