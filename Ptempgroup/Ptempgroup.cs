using System;
using System.Collections.Generic;
using System.IO;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using Newtonsoft.Json;
using Terraria;
using TerrariaApi.Server;
using TShockAPI;
using TShockAPI.DB;

namespace PTempGroup
{
    [ApiVersion(2, 1)]
    public class PTempGroup : TerrariaPlugin
    {
        private string dataFilePath = Path.Combine(TShock.SavePath, "tempgroup_timers.json");
        private Dictionary<string, TempGroupInfo> tempGroupTimers = new Dictionary<string, TempGroupInfo>();

        public override string Name => "Persistent TempGroup";
        public override string Author => "Verza";
        public override string Description => "Enhances Tempgroup command by allowing persistent timers across server restarts.";
        public override Version Version => new Version(1, 0, 0);

        public PTempGroup(Main game) : base(game)
        {
        }

        public override void Initialize()
        {
            ServerApi.Hooks.GameInitialize.Register(this, OnInitialize);
            ServerApi.Hooks.ServerLeave.Register(this, OnServerLeave);
            ServerApi.Hooks.GamePostInitialize.Register(this, OnPostInitialize);
            ServerApi.Hooks.NetGreetPlayer.Register(this, OnGreetPlayer);
        }

        private void OnInitialize(EventArgs args)
        {
            Commands.ChatCommands.Add(new Command("tempgroup", PersistentTempGroupCommand, "ptempgroup"));
            LoadTimers();
        }

        private void OnPostInitialize(EventArgs args)
        {
            ResumeTimers();
        }

        private void OnServerLeave(LeaveEventArgs args)
        {
            SaveTimers();
        }

        private void OnGreetPlayer(GreetPlayerEventArgs args)
        {
            SaveTimers();
        }

        private async void PersistentTempGroupCommand(CommandArgs args)
        {
            if (args.Parameters.Count < 3)
            {
                args.Player.SendErrorMessage("Usage: /ptempgroup <player> <group> <time>");
                return;
            }

            string playerName = args.Parameters[0];
            string groupName = args.Parameters[1];
            string timeStr = args.Parameters[2];

            if (!TryParseTime(timeStr, out int timeInSeconds))
            {
                args.Player.SendErrorMessage("Invalid time format. Use <number>[s/m/h/d] (e.g., 10m for 10 minutes)");
                return;
            }

            var players = TSPlayer.FindByNameOrID(playerName);
            string originalGroup;

            if (players.Count == 1)
            {
                // Player is online
                TSPlayer target = players[0];
                originalGroup = target.Group.Name;
                // Set the temporary group
                TShockAPI.Commands.HandleCommand(TSPlayer.Server, $"/user group \"{playerName}\" {groupName}");
            }
            else
            {
                // Player is offline, fetch from the database
                var userAccount = TShock.UserAccounts.GetUserAccountByName(playerName);
                if (userAccount == null)
                {
                    args.Player.SendErrorMessage("Player not found.");
                    return;
                }
                originalGroup = userAccount.Group;
                // Set the temporary group in the database
                TShock.UserAccounts.SetUserGroup(userAccount, groupName);
            }

            DateTime expiryTime = DateTime.UtcNow.AddSeconds(timeInSeconds);
            tempGroupTimers[playerName] = new TempGroupInfo { ExpiryTime = expiryTime, OriginalGroup = originalGroup };

            // Save the timers
            SaveTimers();

            args.Player.SendSuccessMessage($"Temporarily set {playerName} to {groupName} for {timeStr}.");
            TShock.Log.Info($"Temporarily set {playerName} to {groupName} for {timeStr}. Original group: {originalGroup}");

            // Start the timer with the correct delay
            await StartTimer(playerName, timeInSeconds, originalGroup);
        }

        private bool TryParseTime(string input, out int seconds)
        {
            seconds = 0;
            var match = Regex.Match(input, @"^(?<value>\d+)(?<unit>[smhd])$", RegexOptions.IgnoreCase);
            if (!match.Success)
                return false;

            int value = int.Parse(match.Groups["value"].Value);
            string unit = match.Groups["unit"].Value.ToLower();

            switch (unit)
            {
                case "s":
                    seconds = value;
                    break;
                case "m":
                    seconds = value * 60;
                    break;
                case "h":
                    seconds = value * 3600;
                    break;
                case "d":
                    seconds = value * 86400;
                    break;
                default:
                    return false;
            }

            return true;
        }

        private void SaveTimers()
        {
            try
            {
                File.WriteAllText(dataFilePath, JsonConvert.SerializeObject(tempGroupTimers));
                TShock.Log.Info("Saved timers to file.");
            }
            catch (Exception ex)
            {
                TShock.Log.ConsoleError(ex.ToString());
            }
        }

        private void LoadTimers()
        {
            try
            {
                if (File.Exists(dataFilePath))
                {
                    tempGroupTimers = JsonConvert.DeserializeObject<Dictionary<string, TempGroupInfo>>(File.ReadAllText(dataFilePath));
                    TShock.Log.Info("Loaded timers from file.");
                }
            }
            catch (Exception ex)
            {
                TShock.Log.ConsoleError(ex.ToString());
            }
        }

        private void ResumeTimers()
        {
            foreach (var timer in tempGroupTimers)
            {
                string playerName = timer.Key;
                DateTime expiryTime = timer.Value.ExpiryTime;
                string originalGroup = timer.Value.OriginalGroup;

                if (DateTime.UtcNow >= expiryTime)
                {
                    TShockAPI.Commands.HandleCommand(TSPlayer.Server, $"/user group \"{playerName}\" {originalGroup}");
                    TShock.Log.Info($"Reverted {playerName} to {originalGroup} after expiration.");
                }
                else
                {
                    int remainingTime = (int)(expiryTime - DateTime.UtcNow).TotalSeconds;
                    _ = StartTimer(playerName, remainingTime, originalGroup);
                }
            }
        }

        private async Task StartTimer(string playerName, int delay, string originalGroup)
        {
            TShock.Log.Info($"Starting timer for {playerName} with delay {delay} seconds.");
            await Task.Delay(delay * 1000);

            TShockAPI.Commands.HandleCommand(TSPlayer.Server, $"/user group \"{playerName}\" {originalGroup}");
            TShock.Log.Info($"Reverted {playerName} to {originalGroup} after expiration.");

            // Remove the timer entry
            tempGroupTimers.Remove(playerName);
            SaveTimers();
        }

        private class TempGroupInfo
        {
            public DateTime ExpiryTime { get; set; }
            public string OriginalGroup { get; set; }
        }
    }
}
