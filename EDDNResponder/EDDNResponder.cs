﻿using EddiCore;
using EddiDataDefinitions;
using EddiDataProviderService;
using EddiEvents;
using EddiSpeechService;
using Newtonsoft.Json;
using Newtonsoft.Json.Serialization;
using RestSharp;
using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Net;
using System.Reflection;
using System.Text.RegularExpressions;
using System.Threading;
using System.Windows.Controls;
using Utilities;

namespace EDDNResponder
{
    /// <summary>
    /// A responder for EDDI to provide information to EDDN.
    /// </summary>
    public class EDDNResponder : EDDIResponder
    {
        // Schema reference: https://github.com/EDSM-NET/EDDN/tree/master/schemas

        // We support sending these events to EDDN. These events contain full location data. 
        private static readonly string[] fullLocationJournalSchemaEvents =
        {
            "FSDJump",
            "Location",
            "CarrierJump"
        };

        // We support sending these events to EDDN. These events contain partial location data... location data will need to be enriched. 
        private static readonly string[] partialLocationJournalSchemaEvents =
        {
            "Docked",
            "SAASignalsFound",
            "Scan"
        };

        // These events must be ignored to prevent enriching events with incorrect location data
        private static readonly string[] ignoredEvents =
        {
            "CarrierJumpRequest", // CarrierJumpRequest events describing the system the carrier is jumping too rather than the system we are in
            "FSDTarget", // FSDTarget events describing the system we are targeting rather than the system we are in
            "FSSSignalDiscovered"  // Scan events from the destination system can register after StartJump and before we actually leave the originating system
        };

        // We will strip these personal keys (plus any localized properties) before sending data to EDDN
        private static readonly string[] personalKeys =
        {
            "ActiveFine",
            "CockpitBreach",
            "BoostUsed",
            "FuelLevel",
            "FuelUsed",
            "JumpDist",
            "Wanted",
            "Latitude",
            "Longitude",
            "MyReputation",
            "SquadronFaction",
            "HappiestSystem",
            "HomeSystem"
        };

        // We keep track of the starsystem information locally
        public string systemName { get; private set; }
        public long? systemAddress { get; private set; }
        public decimal? systemX { get; private set; }
        public decimal? systemY { get; private set; }
        public decimal? systemZ { get; private set; }
        public string stationName { get; private set; }
        public long? marketId { get; private set; }

        // We also keep track of game version and active expansions locally
        public string gameVersion { get; private set; }

        public string gameBuild { get; private set; }

        public bool? inHorizons { get; private set; }

        public bool? inOdyssey { get; private set; }

        // Are we in an invalid state?
        public bool invalidState { get; private set; }

        public static readonly List<string> invalidSchemas = new List<string>();

        private StarSystemRepository starSystemRepository;

        public string ResponderName()
        {
            return "EDDN responder";
        }

        public string LocalizedResponderName()
        {
            return EddiEddnResponder.Properties.EddnResources.name;
        }

        public string ResponderDescription()
        {
            return EddiEddnResponder.Properties.EddnResources.desc;
        }

        public EDDNResponder() : this(StarSystemSqLiteRepository.Instance)
        { }

        public EDDNResponder(StarSystemRepository starSystemRepository)
        {
            this.starSystemRepository = starSystemRepository;
            Logging.Info($"Initialized {ResponderName()}");
        }

        public void Handle(Event theEvent)
        {
            if (EDDI.Instance.inTelepresence)
            {
                // We don't do anything whilst in Telepresence
                return;
            }

            if (!string.IsNullOrEmpty(theEvent.raw)) // A null value may indicate a synthetic event used to pass data within EDDI
                                                     // (which should always be ignored)
            {
                IDictionary<string, object> data = Deserializtion.DeserializeData(theEvent.raw);
                string edType = JsonParsing.getString(data, "event");

                // Obtain data that should be handled whether or not the event is from log loading

                // Attempt to obtain available game version data from the active event 
                GetGameVersionData(edType, data);

                // Ignore any events that we've blacklisted for contaminating our location data
                if (ignoredEvents.Contains(edType)) { return; }

                // Except as noted above, always attempt to obtain available location data from the active event 
                GetLocationData(edType, data);

                if (theEvent.fromLoad)
                {
                    // Don't do anything with data acquired during log loading except update our internal state
                    return;
                }

                Logging.Debug("Received event " + theEvent.raw);

                if (edType == "FSSDiscoveryScan")
                {
                    handleFSSDiscoveryScanSchemaEvent(edType, data);
                }
                else if (theEvent is MarketInformationUpdatedEvent)
                {
                    handleMarketInformationUpdatedEvent((MarketInformationUpdatedEvent)theEvent);
                }
                else if (edType == "NavBeaconScan")
                {
                    handleNavBeaconScanSchemaEvent(edType, data);
                }
                else if (edType == "NavRoute")
                {
                    handleNavRouteSchemaEvent(edType, data);
                }
                else if (edType == "ScanBaryCentre")
                {
                    handleScanBaryCentreSchemaEvent(edType, data);
                }
                else if (fullLocationJournalSchemaEvents.Contains(edType) || partialLocationJournalSchemaEvents.Contains(edType))
                {
                    handleJournalSchemaEvents(edType, data);
                }
            }
        }

        private void handleFSSDiscoveryScanSchemaEvent(string edType, IDictionary<string, object> data)
        {
            if (data == null) { return; }
            if (CheckLocationData(edType, data))
            {
                // This event contains a `SystemName` property so we do not need to enrich it with the conventional `StarSystem` property
                if (!data.ContainsKey("StarPos") && systemX != null && systemY != null && systemZ != null)
                {
                    IList<decimal> starpos = new List<decimal>
                    {
                        systemX.Value,
                        systemY.Value,
                        systemZ.Value
                    };
                    data.Add("StarPos", starpos);
                }
                data = AddGameVersionData(data);
                if (data.ContainsKey("Progress")) { data.Remove("Progress"); } // Personal data
                SendToEDDN("https://eddn.edcd.io/schemas/fssdiscoveryscan/1", data);
            }
        }

        private void handleNavBeaconScanSchemaEvent(string edType, IDictionary<string, object> data)
        {
            if (data == null) { return; }
            if (CheckLocationData(edType, data))
            {
                data = EnrichLocationData(edType, data);
                data = AddGameVersionData(data);
                SendToEDDN("https://eddn.edcd.io/schemas/navbeaconscan/1", data);
            }
        }

        private void handleNavRouteSchemaEvent(string edType, IDictionary<string, object> data)
        {
            if (data == null) { return; }
            data = AddGameVersionData(data);
            SendToEDDN("https://eddn.edcd.io/schemas/navroute/1", data);
        }

        private void handleScanBaryCentreSchemaEvent(string edType, IDictionary<string, object> data)
        {
            if (data == null) { return; }
            if (CheckLocationData(edType, data))
            {
                data = EnrichLocationData(edType, data);
                data = AddGameVersionData(data);
                SendToEDDN("https://eddn.edcd.io/schemas/scanbarycentre/1", data);
            }
        }

        private void handleJournalSchemaEvents(string edType, IDictionary<string, object> data)
        {
            if (data == null) { return; }
            if (CheckLocationData(edType, data) && CheckSanity(edType, data))
            {
                data = StripPersonalData(data);
                data = EnrichLocationData(edType, data);
                data = AddGameVersionData(data);
                SendToEDDN("https://eddn.edcd.io/schemas/journal/1", data);
            }
        }

        public bool Start()
        {
            return true;
        }

        public void Stop()
        {
        }

        public void Reload()
        {
        }

        private void ClearLocation()
        {
            systemName = null;
            systemAddress = null;
            systemX = null;
            systemY = null;
            systemZ = null;
            stationName = null;
            marketId = null;
        }

        private void GetGameVersionData(string eventType, IDictionary<string, object> data)
        {
            try
            {
                if (string.Equals("FileHeader", eventType, StringComparison.InvariantCultureIgnoreCase))
                {
                    gameVersion = JsonParsing.getString(data, "gameversion") ?? gameVersion;
                    gameBuild = JsonParsing.getString(data, "build") ?? gameBuild;
                }
                else if (string.Equals("Outfitting", eventType, StringComparison.InvariantCultureIgnoreCase) 
                    || string.Equals("Shipyard", eventType, StringComparison.InvariantCultureIgnoreCase))
                {
                    inHorizons = JsonParsing.getOptionalBool(data, "Horizons") ?? inHorizons;
                    inOdyssey = JsonParsing.getOptionalBool(data, "Odyssey") ?? inOdyssey;
                }
                else if (string.Equals("LoadGame", eventType, StringComparison.InvariantCultureIgnoreCase))
                {
                    gameVersion = JsonParsing.getString(data, "gameversion") ?? gameVersion;
                    gameBuild = JsonParsing.getString(data, "build") ?? gameBuild;
                    inHorizons = JsonParsing.getOptionalBool(data, "Horizons") ?? inHorizons;
                    inOdyssey = JsonParsing.getOptionalBool(data, "Odyssey") ?? inOdyssey;
                }
            }
            catch (Exception ex)
            {
                Logging.Error("Failed to parse Elite Dangerous version data for EDDN", ex);
            }
        }

        private void GetLocationData(string edType, IDictionary<string, object> data)
        {
            // We always start location data fresh when handling events containing complete location data
            if (fullLocationJournalSchemaEvents.Contains(edType))
            {
                ClearLocation();
            }

            try
            {
                systemName = JsonParsing.getString(data, "StarSystem") ?? systemName;

                // Some events are bugged and return a SystemAddress of 1, regardles of the system we are in.
                // We need to ignore data that matches this pattern.
                long? SystemAddress = JsonParsing.getOptionalLong(data, "SystemAddress");
                systemAddress = (SystemAddress > 1 ? SystemAddress : systemAddress);

                data.TryGetValue("StarPos", out object starpos);
                if (starpos != null)
                {
                    List<object> starPos = (List<object>)starpos;
                    systemX = Math.Round(JsonParsing.getDecimal("X", starPos[0]) * 32M) / 32M;
                    systemY = Math.Round(JsonParsing.getDecimal("Y", starPos[1]) * 32M) / 32M;
                    systemZ = Math.Round(JsonParsing.getDecimal("Z", starPos[2]) * 32M) / 32M;
                }

                marketId = JsonParsing.getOptionalLong(data, "MarketID") ?? marketId;
                stationName = JsonParsing.getString(data, "StationName") ?? stationName;
            }
            catch (Exception ex)
            {
                Logging.Error("Failed to parse location data for EDDN", ex);
            }
        }

        private bool CheckLocationData(string edType, IDictionary<string, object> data)
        {
            // Confirm the location data in memory is as accurate as possible when handling an event with partial location data
            if (fullLocationJournalSchemaEvents.Contains(edType) && LocationIsSet()) { return true; }

            // Can only send journal data if we know our current location data is correct
            // If any location data is null, data shall not be sent to EDDN.
            if (LocationIsSet())
            {
                // The `Docked` event doesn't provide system coordinates, and the `Scan`event doesn't provide any system location data.
                // The EDDN journal schema requires that we enrich the journal event data with coordinates and system name (and system address if possible).
                if (data.ContainsKey("BodyName") && !data.ContainsKey("SystemName"))
                {
                    // Apply heuristics to weed out mismatched systems and bodies
                    ConfirmScan(JsonParsing.getString(data, "BodyName"));
                }
                if (!data.ContainsKey("SystemAddress") || !data.ContainsKey("StarPos"))
                {
                    // Out of an overabundance of caution, we do not use data from our saved star systems to enrich the data we send to EDDN, 
                    // but we do use it as an independent check to make sure our system address and coordinates are accurate
                    ConfirmAddressAndCoordinates();
                }

                if (LocationIsSet())
                {
                    invalidState = false;
                    return true;
                }
                
                if (!invalidState)
                {
                    invalidState = true;
#if DEBUG
                    var _ = SpeechService.Instance; // just a dummy to stop the using statement from being pruned
#else
                    Logging.Warn("The EDDN responder is in an invalid state and is unable to send messages.", JsonConvert.SerializeObject(this) + " Event: " + JsonConvert.SerializeObject(data));
                    SpeechService.Instance.Say(null, EddiEddnResponder.Properties.EddnResources.errPosition);
#endif
                }
            }
            return false;
        }

        private bool CheckSanity(string edType, IDictionary<string, object> data)
        {
            // We've already vetted location data via the CheckLocationData method.
            // Perform any additional quality checks we think we need here.
            var passed = true;
            switch (edType)
            {
                case "Docked":
                    // Identify and catch a possible FDev bug that can allow incomplete `Docked` messages
                    // missing a MarketID and many other properties.
                    if (!data.ContainsKey("MarketID")) { passed = false; }

                    // Don't allow messages with a missing StationName.
                    if (data.ContainsKey("StationName") && string.IsNullOrEmpty(JsonParsing.getString(data, "StationName"))) { passed = false; }

                    break;
                case "SAASignalsFound":
                    if (!data.ContainsKey("Signals")) { passed = false; }
                    break;
                case "Scan":
                    if (!data.ContainsKey("ScanType")) { passed = false; }
                    break;
            }
            return passed;
        }

        private bool LocationIsSet()
        {
            return systemName != null && systemAddress != null && systemX != null && systemY != null && systemZ != null;
        }

        private IDictionary<string, object> StripPersonalData(IDictionary<string, object> data)
        {
            // Need to strip a number of personal entries
            foreach (var personalKey in personalKeys) { data.Remove(personalKey); }

            // Need to remove any keys ending with _Localised
            data = data.Where(x => !x.Key.EndsWith("_Localised")).ToDictionary(x => x.Key, x => x.Value);

            // Need to remove personal data from any Dictionary or List type child objects
            IDictionary<string, object> fixedData = new Dictionary<string, object>();
            foreach (KeyValuePair<string, object> item in data)
            {
                if (item.Value is Dictionary<string, object> dict)
                {
                    fixedData.Add(item.Key, StripPersonalData(dict));
                    continue;
                }
                if (item.Value is List<object> list)
                {
                    List<object> newList = new List<object>();
                    for (int i = 0; i < list.Count; i++)
                    {
                        if (list[i] is Dictionary<string, object> listDict)
                        {
                            newList.Add(StripPersonalData(listDict));
                            continue;
                        }
                        newList.Add(list[i]);
                    }
                    fixedData.Add(item.Key, newList);
                    continue;
                }
                fixedData.Add(item);
            }

            return fixedData;
        }

        private IDictionary<string, object> EnrichLocationData(string edType, IDictionary<string, object> data)
        {
            if (!data.ContainsKey("StarSystem"))
            {
                data.Add("StarSystem", systemName);
            }
            if (!data.ContainsKey("SystemAddress"))
            {
                data.Add("SystemAddress", systemAddress);
            }
            if (!data.ContainsKey("StarPos") && systemX != null && systemY != null && systemZ != null)
            {
                IList<decimal> starpos = new List<decimal>
                {
                    systemX.Value,
                    systemY.Value,
                    systemZ.Value
                };
                data.Add("StarPos", starpos);
            }
            return data;
        }

        private IDictionary<string, object> AddGameVersionData(IDictionary<string, object> data)
        {
            if (!data.ContainsKey("horizons") && inHorizons != null)
            {
                data.Add("horizons", inHorizons);
            }
            if (!data.ContainsKey("odyssey") && inOdyssey != null)
            {
                data.Add("odyssey", inOdyssey);
            }
            return data;
        }

        private static void SendToEDDN(string schema, IDictionary<string, object> data)
        {
            try
            {
                EDDNBody body = new EDDNBody
                {
                    header = generateHeader(),
                    schemaRef = schema + (EDDI.Instance.ShouldUseTestEndpoints() ? "/test" : ""),
                    message = data
                };
                if (invalidSchemas.Contains(body.schemaRef))
                {
                    Logging.Warn($"EDDN schema {body.schemaRef} is obsolete, data not sent.", data);
                }
                else
                {
                    Logging.Debug("EDDN message is: " + JsonConvert.SerializeObject(body));
                    sendMessage(body);
                }
            }
            catch (Exception ex)
            {
                data.Add(new KeyValuePair<string, object>("Exception", ex));
                Logging.Error("Unable to send message to EDDN, schema " + schema, data);
            }
        }

        private void handleMarketInformationUpdatedEvent(MarketInformationUpdatedEvent theEvent)
        {
            // This event is triggered by an update to the profile via the Frontier API or via the `Market`, `Outfitting`, or `Shipyard` journal events.
            // Check to make sure the marketId from the event matches our current station's marketId before continuing
            if (ConfirmStationMatchesLocation(theEvent))
            {
                // When we dock we have access to commodity, outfitting, and shipyard information
                sendCommodityInformation(theEvent);
                sendOutfittingInformation(theEvent);
                sendShipyardInformation(theEvent);
            }
        }

        private void sendCommodityInformation(MarketInformationUpdatedEvent theEvent)
        {
            if (theEvent.commodityQuotes != null)
            {
                // An empty list is permissible, e.g. if a fleet carrier isn't buying or selling any commodities.
                var commodities = theEvent.commodityQuotes
                    .Where(c => c.IsMarketable())
                    .ToList();

                IDictionary<string, object> data = new Dictionary<string, object>
                    {
                        { "timestamp", Dates.FromDateTimeToString(theEvent.timestamp) },
                        { "systemName", theEvent.starSystem },
                        { "stationName", theEvent.stationName },
                        { "marketId", theEvent.marketId },
                        { "horizons", theEvent.inHorizons},
                        { "odyssey", theEvent.inOdyssey}

                    };
                data.Add("commodities", commodities);
                if (theEvent.prohibitedCommodities?.Count > 0)
                {
                    data.Add("prohibited", theEvent.prohibitedCommodities);
                }

                SendToEDDN("https://eddn.edcd.io/schemas/commodity/3", data);
            }
        }

        private void sendOutfittingInformation(MarketInformationUpdatedEvent theEvent)
        {
            if (theEvent.outfittingModules?.Count > 0)
            {
                List<string> eddnModules = new List<string>();
                foreach (string moduleEdName in theEvent.outfittingModules)
                {
                    if (!EddiDataDefinitions.Module.PowerPlayModules.Contains(moduleEdName)
                        && (moduleEdName.StartsWith("Int_") || moduleEdName.StartsWith("Hpt_") || moduleEdName.Contains("_Armour_"))
                        && moduleEdName != "Int_PlanetApproachSuite")
                    {
                        eddnModules.Add(moduleEdName);
                    }
                }

                // Only send the message if we have modules
                if (eddnModules.Count > 0)
                {
                    IDictionary<string, object> data = new Dictionary<string, object>
                    {
                        { "timestamp", Dates.FromDateTimeToString(theEvent.timestamp) },
                        { "systemName", theEvent.starSystem },
                        { "stationName", theEvent.stationName },
                        { "marketId", theEvent.marketId },
                        { "modules", eddnModules },
                        { "horizons", theEvent.inHorizons},
                        { "odyssey", theEvent.inOdyssey}
                    };

                    SendToEDDN("https://eddn.edcd.io/schemas/outfitting/2", data);
                }
            }
        }

        private void sendShipyardInformation(MarketInformationUpdatedEvent theEvent)
        {
            // Only send the message if we have ships
            if (theEvent.shipyardModels?.Count > 0)
            {
                IDictionary<string, object> data = new Dictionary<string, object>
                    {
                        { "timestamp", Dates.FromDateTimeToString(theEvent.timestamp) },
                        { "systemName", theEvent.starSystem },
                        { "stationName", theEvent.stationName },
                        { "marketId", theEvent.marketId },
                        { "ships", theEvent.shipyardModels },
                        { "horizons", theEvent.inHorizons},
                        { "odyssey", theEvent.inOdyssey},
                        { "allowCobraMkIV", theEvent.allowCobraMkIV }
                    };

                SendToEDDN("https://eddn.edcd.io/schemas/shipyard/2", data);
            }
        }


        private static string generateUploaderId()
        {
            // Uploader ID is a hash of the commander's name
            //System.Security.Cryptography.SHA256Managed crypt = new System.Security.Cryptography.SHA256Managed();
            //StringBuilder hash = new StringBuilder();
            //string uploader = (EDDI.Instance.Cmdr == null ? "commander" : EDDI.Instance.Cmdr.name);
            //byte[] crypto = crypt.ComputeHash(Encoding.UTF8.GetBytes(uploader), 0, Encoding.UTF8.GetByteCount(uploader));
            //foreach (byte theByte in crypto)
            //{
            //    hash.Append(theByte.ToString("x2"));
            //}
            //return hash.ToString();
            return EDDI.Instance.Cmdr == null || EDDI.Instance.Cmdr.name == null ? "Unknown commander" : EDDI.Instance.Cmdr.name;
        }

        private static EDDNHeader generateHeader()
        {
            EDDNHeader header = new EDDNHeader
            {
                softwareName = Constants.EDDI_NAME,
                softwareVersion = Constants.EDDI_VERSION.ToString(),
                uploaderID = generateUploaderId()
            };
            return header;
        }

        private static void sendMessage(EDDNBody body)
        {
            var client = new RestClient("https://eddn.edcd.io:4430/");
            var request = new RestRequest("upload/", Method.POST);
            var msgBody = JsonConvert.SerializeObject(body, new JsonSerializerSettings { ContractResolver = new EDDNContractResolver() });
            request.AddParameter("application/json", msgBody, ParameterType.RequestBody);

            Logging.Debug("Sending " + msgBody);

            Thread thread = new Thread(() =>
            {
                IRestResponse response = null;
                try
                {
                    response = client.Execute(request);
                    Logging.Debug("Response content is " + response.Content);
                    switch (response.StatusCode)
                    {
                        // Invalid status codes are defined at https://github.com/EDCD/EDDN/blob/live/schemas/README-EDDN-schemas.md
                        case HttpStatusCode.BadRequest: // Code 400
                        {
                            throw new ArgumentException();
                        }
                        case HttpStatusCode.RequestTimeout: // Code 408
                        case HttpStatusCode.GatewayTimeout: // Code 504
                        {
                            Logging.Debug("Request timed out, retrying in 30 seconds.");
                            System.Threading.Tasks.Task.Run(() =>
                            {
                                Thread.Sleep(TimeSpan.FromSeconds(30));
                                sendMessage(body);
                            });
                            break;
                        }
                        case HttpStatusCode.RequestEntityTooLarge: // Code 413
                        {
                            // Payload too large. Retry with G-Zipped data
                            Logging.Warn("EDDN service is unable to process the message. Payload too large. Retrying with compressed data.");
                            request = new RestRequest("upload/", Method.POST);
                            request.AddHeader("Content-Encoding", "gzip");
                            var messageConverterStream = new MemoryStream(byte.Parse(msgBody));
                            using (GZipStream compressionStream = new GZipStream(messageConverterStream, CompressionMode.Compress))
                            {
                                request.AddParameter("application/json", compressionStream.AsOutputStream(), ParameterType.RequestBody);
                            }
                            response = client.Execute(request);
                            Logging.Debug("Response content is " + response.Content);
                            if (response.StatusCode != HttpStatusCode.Accepted)
                            {
                                throw new WebException("Failed to resend to EDDN service with compressed data.");
                            }
                            break;
                        }
                        case HttpStatusCode.UpgradeRequired: // Code 426
                        {
                            // Note that this deviates from the typical usage of code 426
                            // (which typically indicates that this client is using an obsolete security protocol.
                            invalidSchemas.Add(body.schemaRef);
                            Logging.Warn(
                                $"EDDN service is unable to process the message. Schema {body.schemaRef} is obsolete.");
                            break;
                        }
                        case HttpStatusCode.ServiceUnavailable: // Code 503
                        {
                            Logging.Debug("EDDN service is unavailable, retrying in 2 minutes");
                            System.Threading.Tasks.Task.Run(() =>
                            {
                                Thread.Sleep(TimeSpan.FromMinutes(2));
                                sendMessage(body);
                            });
                            break;
                        }
                        default:
                        {
                            if ((int)response.StatusCode >= 400)
                            {
                                throw new WebException("Unexpected EDDN service response");
                            }
                            break;
                        }
                    }
                }
                catch (ThreadAbortException)
                {
                    Logging.Debug("Thread aborted");
                }
                catch (Exception ex)
                {
                    Dictionary<string, object> data = new Dictionary<string, object>
                    {
                        { "eddnMessage", JsonConvert.SerializeObject(body?.message) },
                        { "Response", response?.Content },
                        { "Exception", ex }
                    };
                    Logging.Error("Failed to send data to EDDN", data);
                }
            })
            {
                Name = "EDDN message",
                IsBackground = true
            };
            thread.Start();
        }

        public UserControl ConfigurationTabItem()
        {
            return null;
        }

        private bool ConfirmAddressAndCoordinates()
        {
            if (systemName != null)
            {
                StarSystem system;
                if (systemName == EDDI.Instance.CurrentStarSystem?.systemname)
                {
                    system = EDDI.Instance.CurrentStarSystem;
                }
                else
                {
                    system = starSystemRepository.GetOrCreateStarSystem(systemName);
                }
                if (system != null)
                {
                    if (systemAddress != system.systemAddress)
                    {
                        systemAddress = null;
                    }
                    if (systemX != system.x || systemY != system.y || systemZ != system.z)
                    {
                        systemX = null;
                        systemY = null;
                        systemZ = null;
                    }
                }
                else
                {
                    ClearLocation();
                }
            }
            return systemAddress != null && systemX != null && systemY != null && systemZ != null;
        }

        private bool ConfirmScan(string bodyName)
        {
            if (bodyName != null && systemName != null)
            {
                if (bodyName.StartsWith(systemName))
                {
                    // If the system name is a subset of the body name, we're probably in the right place.
                    return true;
                }
                else
                {
                    // If the body doesn't start with the system name, it should also 
                    // not match a naming pattern for a procedurally generated name.
                    // If it does, it's (probably) in the wrong place.
                    Regex isProcGenName = new Regex(@"[A-Z][A-Z]-[A-Z] [a-h][0-9]");
                    if (!isProcGenName.IsMatch(bodyName))
                    {
                        return true;
                    }
                }
            }
            // Set values to null if data can't be confirmed. 
            ClearLocation();
            return false;
        }

        private bool ConfirmStationMatchesLocation(MarketInformationUpdatedEvent theEvent)
        {
            if (theEvent.starSystem == systemName && theEvent.stationName == stationName && theEvent.marketId == marketId)
            {
                return true;
            }
            return false;
        }
    }

    public sealed class EDDNContractResolver : DefaultContractResolver
    {
        protected override JsonProperty CreateProperty(MemberInfo member, MemberSerialization memberSerialization)
        {
            JsonProperty property = base.CreateProperty(member, memberSerialization);

            if (property.PropertyType == typeof(CommodityBracket?))
            {
                // The EDDN schema requires a value of "" rather than null for commodity brackets
                property.ValueProvider = new NullToEmptyStringValueProvider(property.ValueProvider);
            }

            return property;
        }

        sealed class NullToEmptyStringValueProvider : IValueProvider
        {
            private readonly IValueProvider Provider;

            public NullToEmptyStringValueProvider(IValueProvider provider)
            {
                Provider = provider ?? throw new ArgumentNullException(nameof(provider));
            }

            public object GetValue(object target)
            {
                return Provider.GetValue(target) ?? "";
            }

            public void SetValue(object target, object value)
            {
                Provider.SetValue(target, value);
            }
        }
    }
}
