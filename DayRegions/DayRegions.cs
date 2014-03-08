using System;
using System.Collections.Generic;
using System.Linq;
using System.IO;
using System.Threading;
using System.Text;
using System.Data;
using Terraria;
using TerrariaApi.Server;
using TShockAPI;
using TShockAPI.DB;
using Mono.Data.Sqlite;
using MySql.Data.MySqlClient;

namespace DayRegions
{

    [ApiVersion(1, 15)]
    public class DayRegions : TerrariaPlugin
    {
        public static IDbConnection db;
        public static SqlTableCreator SQLcreator;
        public static SqlTableEditor SQLeditor;
        public static List<Region> RegionList = new List<Region>();
        public static List<int> DayClients = new List<int>();
        private static DateTime lastUpdate = DateTime.UtcNow;
        private bool initialized = false;

        private void SetupDb()
        {
            if (TShock.Config.StorageType.ToLower() == "sqlite")
            {
                string sql = Path.Combine(TShock.SavePath, "InanZen_DB.sqlite");
                db = new SqliteConnection(string.Format("uri=file://{0},Version=3", sql));
            }
            else if (TShock.Config.StorageType.ToLower() == "mysql")
            {
                try
                {
                    var hostport = TShock.Config.MySqlHost.Split(':');
                    db = new MySqlConnection();
                    db.ConnectionString =
                        String.Format("Server={0}; Port={1}; Database={2}; Uid={3}; Pwd={4};",
                                      hostport[0],
                                      hostport.Length > 1 ? hostport[1] : "3306",
                                      TShock.Config.MySqlDbName,
                                      TShock.Config.MySqlUsername,
                                      TShock.Config.MySqlPassword
                            );
                }
                catch (MySqlException ex)
                {
                    Log.Error(ex.ToString());
                    throw new Exception("MySql not setup correctly");
                }
            }
            else
            {
                throw new Exception("Invalid storage type");
            }

            SQLcreator = new SqlTableCreator(db, new SqliteQueryCreator());
            SQLeditor = new SqlTableEditor(db, new SqliteQueryCreator());
            DayRegions_Create();
        }
        public override string Name
        {
            get { return "DayRegions"; }
        }
        public override string Author
        {
            get { return "by InanZen"; }
        }
        public override string Description
        {
            get { return "Provides overworld background to specified regions"; }
        }
        public override Version Version
        {
            get { return new Version(1, 0, 1); }
        }
        public override void Initialize()
        {
            ServerApi.Hooks.GameUpdate.Register(this, OnUpdate);
            ServerApi.Hooks.NetSendData.Register(this, SendData);
            
            SetupDb();
            Commands.ChatCommands.Add(new Command("tshock.world.editregion", DayregionCommand, "dayregion"));
        }
        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                ServerApi.Hooks.GameUpdate.Deregister(this, OnUpdate);
                ServerApi.Hooks.NetSendData.Deregister(this, SendData);
            }
            base.Dispose(disposing);
        }
        public DayRegions(Main game)
            : base(game)
        {
            Order = -1;
        }

        private static void OnWorldLoad()
        {
            Console.WriteLine("On World Load ....");
            DayRegions_Read();
        }
        public void SendData(SendDataEventArgs e)
        {
            if (e.MsgId == PacketTypes.WorldInfo)
            {
                if (e.remoteClient == -1)
                {
                    for (int i = 0; i < TShock.Players.Length; i++)
                    {
                        if (TShock.Players[i] != null && TShock.Players[i].Active && !DayClients.Contains(TShock.Players[i].Index))
                            TShock.Players[i].SendData(PacketTypes.WorldInfo);
                    }
                    e.Handled = true;
                }
            }
        }
        private void OnUpdate(EventArgs args)
        {
            if ((DateTime.UtcNow - lastUpdate).TotalMilliseconds >= 1500)
            {
                lastUpdate = DateTime.UtcNow;
                if (!initialized && Main.worldID > 0)
                {
                    initialized = true;
                    OnWorldLoad();
                }
                try
                {
                    foreach (TSPlayer player in TShock.Players)
                    {
                        if (player != null && player.Active)
                        {
                            foreach (Region region in RegionList)
                            {
                                if (region.InArea(player.TileX, player.TileY))
                                {
                                    if (!DayClients.Contains(player.Index))
                                    {
                                        DayClients.Add(player.Index);
                                        double oldWS = Main.worldSurface;
                                        double oldRL = Main.rockLayer;
                                        Main.worldSurface = region.Area.Bottom;
                                        Main.rockLayer = region.Area.Bottom + 10;
                                        player.SendData(PacketTypes.WorldInfo);
                                        Main.worldSurface = oldWS;
                                        Main.rockLayer = oldRL;
                                    }
                                }
                                else if (DayClients.Contains(player.Index))
                                {
                                    DayClients.Remove(player.Index);
                                    player.SendData(PacketTypes.WorldInfo);
                                }
                            }
                        }
                    }
                }
                catch (Exception e)
                {
                    Console.WriteLine(e.Message);
                    Log.Warn(e.Message);
                }
            }
        }
        public static void DayregionCommand(CommandArgs args)
        {
            if (args.Parameters.Count > 1)
            {
                var region = TShock.Regions.GetRegionByName(args.Parameters[1]);
                if (region != null && region.Name != "")
                {
                    if (args.Parameters[0] == "add" && DayRegions_Add(region.Name))
                    {
                        RegionList.Add(region);
                        args.Player.SendMessage(String.Format("Region '{0}' added to Day Region list", region.Name), Color.BurlyWood);
                        return;
                    }
                    else if (args.Parameters[0] == "del")
                    {
                        DayRegions_Delete(region);
                        args.Player.SendMessage(String.Format("Region '{0}' deleted from Day Region list", region.Name), Color.BurlyWood);
                        return;
                    }
                    return;
                }
                else
                {
                    args.Player.SendMessage(String.Format("Region '{0}' not found", args.Parameters[1]), Color.Red);
                    return;
                }
            }
            args.Player.SendMessage("Syntax: /dayregion [add | del] \"region name\"", Color.Red);
        }
        private static void DayRegions_Read()
        {
            QueryResult reader;
            lock (db)
            {
                reader = db.QueryReader("Select Region from DayRegions");
            }
            lock (RegionList)
            {
                while (reader.Read())
                {
                    var region = TShock.Regions.GetRegionByName(reader.Get<string>("Region"));
                    if (region != null && region.Name != "")
                        RegionList.Add(region);
                }
                reader.Dispose();
            }
        }
        private static void DayRegions_Create()
        {
            var table = new SqlTable("DayRegions",
                new SqlColumn("ID", MySql.Data.MySqlClient.MySqlDbType.Int32) { Primary = true, AutoIncrement = true, NotNull = true },
                new SqlColumn("Region", MySql.Data.MySqlClient.MySqlDbType.VarChar) { Unique = true, Length = 30 }
            );
            SQLcreator.EnsureExists(table);
        }
        private static bool DayRegions_Add(string name)
        {
            List<SqlValue> values = new List<SqlValue>();
            values.Add(new SqlValue("Region", "'" + name + "'"));
            lock (db)
            {
                SQLeditor.InsertValues("DayRegions", values);
            }
            return true;
        }
        public static void DayRegions_Delete(Region region)
        {
            lock (db)
            {
                db.Query("Delete from DayRegions where Region = @0", region.Name);
            }
            lock (RegionList)
            {
                RegionList.Remove(region);
            }
        }
    }
}