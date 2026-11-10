using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading;
using Crestron.SimplSharp;
using Crestron.SimplSharpPro.DeviceSupport;
using Epi.Display.Lg;
using PepperDash.Core;
using PepperDash.Core.Logging;
using PepperDash.Essentials.Core;
using PepperDash.Essentials.Core.Bridges;
using PepperDash.Essentials.Core.DeviceInfo;
using PepperDash.Essentials.Core.DeviceTypeInterfaces;
using PepperDash.Essentials.Core.Queues;
using TwoWayDisplayBase = PepperDash.Essentials.Devices.Common.Displays.TwoWayDisplayBase;
using Interlocked = System.Threading.Interlocked;


namespace PepperDash.Essentials.Plugins.Lg.Display
{
    public class LgDisplayController : TwoWayDisplayBase, IBasicVolumeWithFeedback, ICommunicationMonitor,
        IBridgeAdvanced, IHasInputs<string>, IBasicVideoMuteWithFeedback, IWarmingCooling, IDeviceInfoProvider
    {
        /// <summary>
        /// The display's address and configured MAC address, and the serial number and software
        /// version it reports. One instance for the life of the device, so what is written to it is
        /// what readers see.
        /// </summary>
        public DeviceInfo DeviceInfo { get; } = new DeviceInfo();

        public event DeviceInfoChangeHandler DeviceInfoChanged;

        GenericQueue receiveQueue;
        public const int InputPowerOn = 101;
        public const int InputPowerOff = 102;
        public static List<string> InputKeys = new List<string>();
        public List<BoolFeedback> InputFeedback;
        public IntFeedback InputNumberFeedback;
        private RoutingInputPort currentInputPort;
        private List<bool> inputFeedback;
        private int inputNumber;
        private bool isCoolingDown;
        private bool isMuted;
        private bool isSerialComm;
        private bool isWarmingUp;
        private bool inputSwitchPending;
        private bool powerOnPending;
        private string lastCommandPrefix;
        private int lastVolumeSent;
        private bool powerIsOn;
        private bool videoIsMuted;
        private ActionIncrementer volumeIncrementer;
        private bool volumeIsRamping;
        private ushort volumeLevelForSig;
        private readonly bool smallDisplay;
        private readonly bool overrideWol;
        private readonly string wolBroadcastAddress;
        private readonly string wolMacAddress;
        private readonly ushort wolPort;
        private GenericUdpServer wolServer;
        private readonly LgDisplayPropertiesConfig config;

        // Keycode-secured network IP control (webOS displays with an IP Control Setup menu). The display
        // answers one command at a time without echoing it, so commands are sent strictly in sequence.
        private const int IpControlResponseTimeoutMs = 3000;
        private const int IpControlQueueCapacity = 50;
        private const string IpControlVolumePlaceholder = "VOLUME_CONTROL";
        private LgIpControlCipher ipControlCipher;
        private BlockingCollection<string> ipControlCommands;
        private readonly object ipControlLock = new object();
        private readonly AutoResetEvent ipControlResponseReceived = new AutoResetEvent(false);
        private string ipControlCommandInFlight;
        private string queuedIpControlVolumeCommand;
        private bool ipControlDecryptWarningLogged;
        // POWER_STATUS gives the real power state; until a display has answered it, CURRENT_APP stands in.
        private bool ipControlPowerStatusSupported;
        private string lastIpControlPowerStatus;


        public LgDisplayController(string key, string name, LgDisplayPropertiesConfig config, IBasicCommunication comms)
            : base(key, name)
        {
            Communication = comms;

            receiveQueue = new GenericQueue(key + "-queue");

            this.config = config;
            var props = config;
            if (props == null)
            {
                this.LogError("Display configuration must be included");
                return;
            }
            smallDisplay = props.SmallDisplay;
            Id = !string.IsNullOrEmpty(props.Id) ? props.Id : "01";
            upperLimit = props.volumeUpperLimit;
            lowerLimit = props.volumeLowerLimit;
            overrideWol = props.OverrideWol;
            wolMacAddress = props.macAddress;
            wolBroadcastAddress = props.WolBroadcastAddress;
            wolPort = props.WolPort ?? 9;
            pollIntervalMs = props.pollIntervalMs > 1999 ? props.pollIntervalMs : 10000;
            coolingTimeMs = props.coolingTimeMs > 0 ? props.coolingTimeMs : 10000;
            warmingTimeMs = props.warmingTimeMs > 0 ? props.warmingTimeMs : 8000;

            if (!string.IsNullOrWhiteSpace(props.Keycode))
            {
                var keycode = props.Keycode.Trim().ToUpperInvariant();
                if (LgIpControlCipher.IsValidKeycode(keycode))
                {
                    ipControlCipher = new LgIpControlCipher(keycode);
                }
                else
                {
                    this.LogError("The keycode must be the {Length} letters and digits shown under IP Control Setup on the display; keycode control is disabled",
                        LgIpControlCipher.KeycodeLength);
                }
            }

            InputNumberFeedback = new IntFeedback("inputNumber", () =>
            {
                return InputNumber;
            });

            if (comms is GenericTcpIpClient tcp && !string.IsNullOrEmpty(tcp.Hostname))
            {
                DeviceInfo.IpAddress = tcp.Hostname;
                DeviceInfo.HostName = tcp.Hostname;
            }

            // The address Wake-on-LAN is configured with, until the display says otherwise.
            DeviceInfo.MacAddress = LgDeviceInfoReply.FormatMac(wolMacAddress);

            Init();
        }

        /// <summary>
        /// Asks the display for its serial number and software version. The plain command set only;
        /// the keycode protocol has no such query, so a display controlled that way reports its
        /// address and configured MAC address alone.
        /// </summary>
        public void UpdateDeviceInfo()
        {
            if (UsesIpControlKeycode)
            {
                return;
            }

            SendData(LgDeviceInfoReply.SerialNumberQuery(Id));
            SendData(LgDeviceInfoReply.SoftwareVersionQuery(Id));
        }

        /// <summary>Asks again only while something is still missing, so polling stays as it was.</summary>
        private void UpdateDeviceInfoIfMissing()
        {
            if (string.IsNullOrEmpty(DeviceInfo.SerialNumber) || string.IsNullOrEmpty(DeviceInfo.FirmwareVersion))
            {
                UpdateDeviceInfo();
            }
        }

        private void SetDeviceInfo(Action<DeviceInfo> set, string value)
        {
            if (value == null) return;

            set(DeviceInfo);
            DeviceInfoChanged?.Invoke(this, new DeviceInfoEventArgs(DeviceInfo));
        }

        public IBasicCommunication Communication { get; private set; }
        public CommunicationGather PortGather { get; private set; }

        public string Id { get; private set; }

        public bool PowerIsOn
        {
            get { return powerIsOn; }
            set
            {
                if (powerIsOn == value)
                {
                    return;
                }

                powerIsOn = value;
                PowerIsOnFeedback.FireUpdate();

                // Some displays answer the identity queries only when on, so ask again at power on.
                if (powerIsOn)
                {
                    UpdateDeviceInfoIfMissing();
                }
            }
        }

        public bool IsWarmingUp
        {
            get { return isWarmingUp; }
            set
            {
                if (isWarmingUp == value) return;

                isWarmingUp = value;

                if (isWarmingUp)
                {
                    WarmupTimer = new System.Timers.Timer(WarmupTime) { AutoReset = false };
                    WarmupTimer.Elapsed += (s, e) =>
                    {
                        IsWarmingUp = false;

                        if (!inputSwitchPending)
                        {
                            InputGet();
                        }
                    };
                    WarmupTimer.Start();
                }
                else if (WarmupTimer != null)
                {
                    WarmupTimer.Stop();
                    WarmupTimer.Dispose();
                    WarmupTimer = null;
                }

                IsWarmingUpFeedback.FireUpdate();
            }
        }

        public bool IsCoolingDown
        {
            get { return isCoolingDown; }
            set
            {
                if (isCoolingDown == value) return;

                isCoolingDown = value;

                if (isCoolingDown)
                {
                    CooldownTimer = new System.Timers.Timer(CooldownTime) { AutoReset = false };
                    CooldownTimer.Elapsed += (s, e) => { IsCoolingDown = false; };
                    CooldownTimer.Start();
                }
                else if (CooldownTimer != null)
                {
                    CooldownTimer.Stop();
                    CooldownTimer.Dispose();
                    CooldownTimer = null;
                }

                IsCoolingDownFeedback.FireUpdate();
            }
        }

        public bool IsMuted
        {
            get { return isMuted; }
            set
            {
                isMuted = value;
                MuteFeedback.FireUpdate();
            }
        }
        public bool VideoIsMuted
        {
            get { return videoIsMuted; }
            set
            {
                videoIsMuted = value;
                VideoMuteIsOn.FireUpdate();
            }
        }

        private readonly int lowerLimit;
        private readonly int upperLimit;
        private readonly uint coolingTimeMs;
        private readonly uint warmingTimeMs;
        private readonly long pollIntervalMs;

        public int InputNumber
        {
            get { return inputNumber; }
            private set
            {
                if (inputNumber == value) return;

                inputNumber = value;
                InputNumberFeedback.FireUpdate();
                UpdateBooleanFeedback(value);
            }
        }

        private bool ScaleVolume { get; set; }

        protected override Func<bool> PowerIsOnFeedbackFunc
        {
            get { return () => PowerIsOn; }
        }

        protected override Func<bool> IsCoolingDownFeedbackFunc
        {
            get { return () => IsCoolingDown; }
        }

        protected override Func<bool> IsWarmingUpFeedbackFunc
        {
            get { return () => IsWarmingUp; }
        }

        protected override Func<string> CurrentInputFeedbackFunc
        {
            get { return () => currentInputPort != null ? currentInputPort.Key : string.Empty; }
        }

        #region IBasicVolumeWithFeedback Members

        /// <summary>
        /// Volume Level Feedback Property
        /// </summary>
        public IntFeedback VolumeLevelFeedback { get; private set; }

        /// <summary>
        /// Volume Mute Feedback Property
        /// </summary>
        public BoolFeedback MuteFeedback { get; private set; }

        /// <summary>
        /// Scales the level to the range of the display and sends the command
        /// Set: "kf [SetID] [Range 0x00 - 0x64]"
        /// </summary>
        /// <param name="level"></param>
        public void SetVolume(ushort level)
        {
            int scaled;
            lastVolumeSent = level;
            if (!ScaleVolume)
            {
                scaled = (int)NumericalHelpers.Scale(level, 0, 65535, 0, 100);
            }
            else
            {
                scaled = (int)NumericalHelpers.Scale(level, 0, 65535, lowerLimit, upperLimit);
            }

            if (UsesIpControlKeycode)
            {
                SendIpControlVolume(scaled);
                return;
            }

            SendData(string.Format("kf {0} {1}", Id, scaled));
        }

        /// <summary>
        /// Set Mute On
        /// </summary>
        public void MuteOn()
        {
            if (UsesIpControlKeycode)
            {
                SendIpControl("VOLUME_MUTE on");
                return;
            }

            SendData(string.Format("ke {0} {1}", Id, smallDisplay ? "0" : "00"));
        }

        /// <summary>
        /// Set Mute Off
        /// </summary>
        public void MuteOff()
        {
            if (UsesIpControlKeycode)
            {
                SendIpControl("VOLUME_MUTE off");
                return;
            }

            SendData(string.Format("ke {0} {1}", Id, smallDisplay ? "1" : "01"));
        }

        /// <summary>
        /// Toggle Current Mute State
        /// </summary>
        public void MuteToggle()
        {
            if (IsMuted)
            {
                MuteOff();
            }
            else
            {
                MuteOn();
            }
        }

        /// <summary>
        /// Decrement Volume on Press
        /// </summary>
        /// <param name="pressRelease"></param>
        public void VolumeDown(bool pressRelease)
        {
            if (pressRelease)
            {
                volumeIncrementer.StartDown();
                volumeIsRamping = true;
            }
            else
            {
                volumeIsRamping = false;
                volumeIncrementer.Stop();
            }
        }

        /// <summary>
        /// Increment Volume on press
        /// </summary>
        /// <param name="pressRelease"></param>
        public void VolumeUp(bool pressRelease)
        {
            if (pressRelease)
            {
                volumeIncrementer.StartUp();
                volumeIsRamping = true;
            }
            else
            {
                volumeIsRamping = false;
                volumeIncrementer.Stop();
            }
        }

        #endregion

        #region IBasicVideoMuteWithFeedback Members

        public BoolFeedback VideoMuteIsOn { get; private set; }

        /// <summary>
        /// Set Video Mute On
        /// </summary>
        public void VideoMuteOn()
        {
            if (UsesIpControlKeycode)
            {
                SendIpControl("SCREEN_MUTE videomuteon");
                return;
            }

            SendData(string.Format("kd {0} {1}", Id, smallDisplay ? "1" : "01"));
        }

        /// <summary>
        /// Set Video Mute Off
        /// </summary>
        public void VideoMuteOff()
        {
            if (UsesIpControlKeycode)
            {
                SendIpControl("SCREEN_MUTE allmuteoff");
                return;
            }

            SendData(string.Format("kd {0} {1}", Id, smallDisplay ? "0" : "00"));
        }

        /// <summary>
        /// Toggle Current Video Mute State
        /// </summary>
        public void VideoMuteToggle()
        {
            if (VideoIsMuted)
            {
                VideoMuteOff();
            }
            else
            {
                VideoMuteOn();
            }
        }

        public void VideoMuteGet()
        {
            // Keycode IP control has no video mute query; feedback follows the commands sent.
            if (UsesIpControlKeycode) return;

            SendData(string.Format("kd {0} FF", Id));
        }

        #endregion

        #region IBridgeAdvanced Members

        public void LinkToApi(BasicTriList trilist, uint joinStart, string joinMapKey, EiscApiAdvanced bridge)
        {
            var joinMap = new LgDisplayBridgeJoinMap(joinStart);

            // This adds the join map to the collection on the bridge
            if (bridge != null)
            {
                bridge.AddJoinMap(Key, joinMap);
            }

            var customJoins = JoinMapHelper.TryGetJoinMapAdvancedForDevice(joinMapKey);
            if (customJoins != null)
            {
                joinMap.SetCustomJoinData(customJoins);
            }

            this.LogInformation("Linking to Trilist '{TrilistId}'", trilist.ID.ToString("X"));
            this.LogInformation("Linking to Bridge Type {BridgeType}", GetType().Name);

            // links to bridge
            // device name
            trilist.SetString(joinMap.Name.JoinNumber, Name);

            //var twoWayDisplay = this as TwoWayDisplayBase;
            //trilist.SetBool(joinMap.IsTwoWayDisplay.JoinNumber, twoWayDisplay != null);

            if (CommunicationMonitor != null)
            {
                CommunicationMonitor.IsOnlineFeedback.LinkInputSig(trilist.BooleanInput[joinMap.IsOnline.JoinNumber]);
            }

            // power off
            trilist.SetSigTrueAction(joinMap.PowerOff.JoinNumber, PowerOff);
            PowerIsOnFeedback.LinkComplementInputSig(trilist.BooleanInput[joinMap.PowerOff.JoinNumber]);

            // power on 
            trilist.SetSigTrueAction(joinMap.PowerOn.JoinNumber, PowerOn);
            PowerIsOnFeedback.LinkInputSig(trilist.BooleanInput[joinMap.PowerOn.JoinNumber]);

            IsCoolingDownFeedback.LinkInputSig(trilist.BooleanInput[joinMap.IsCoolingDown.JoinNumber]);
            IsWarmingUpFeedback.LinkInputSig(trilist.BooleanInput[joinMap.IsWarmingUp.JoinNumber]);

            // input (digital select, digital feedback, names)
            for (var i = 0; i < InputPorts.Count; i++)
            {
                var inputIndex = i;
                var input = InputPorts.ElementAt(inputIndex);

                if (input == null) continue;

                trilist.SetSigTrueAction((ushort)(joinMap.InputSelectOffset.JoinNumber + inputIndex), () =>
                {
                    SetInput = inputIndex + 1;
                });

                var inputName = input.Key;
                if (Inputs?.Items != null && input.FeedbackMatchObject is string fbMatch && Inputs.Items.TryGetValue(fbMatch, out var selectableItem))
                {
                    inputName = selectableItem.Name;
                }
                trilist.StringInput[(ushort)(joinMap.InputNamesOffset.JoinNumber + inputIndex)].StringValue = inputName ?? string.Empty;

                if (InputFeedback != null && inputIndex < InputFeedback.Count)
                {
                    InputFeedback[inputIndex].LinkInputSig(trilist.BooleanInput[joinMap.InputSelectOffset.JoinNumber + (uint)inputIndex]);
                }
            }

            // input (analog select)
            trilist.SetUShortSigAction(joinMap.InputSelect.JoinNumber, analogValue =>
            {
                SetInput = analogValue;
            });

            // input (analog feedback)
            if (InputNumberFeedback != null)
                InputNumberFeedback.LinkInputSig(trilist.UShortInput[joinMap.InputSelect.JoinNumber]);

            if (CurrentInputFeedback != null)
                CurrentInputFeedback.OutputChange += (sender, args) => this.LogDebug("CurrentInputFeedback: {Value}", args.StringValue);

            // bridge online change
            trilist.OnlineStatusChange += (sender, args) =>
            {
                if (!args.DeviceOnLine) return;

                // device name
                trilist.SetString(joinMap.Name.JoinNumber, Name);

                PowerIsOnFeedback.FireUpdate();

                if (CurrentInputFeedback != null)
                    CurrentInputFeedback.FireUpdate();

                if (InputNumberFeedback != null)
                    InputNumberFeedback.FireUpdate();

                for (var i = 0; i < InputPorts.Count; i++)
                {
                    var inputIndex = i;
                    if (InputFeedback != null)
                        InputFeedback[inputIndex].FireUpdate();
                }
            };
        }

        #endregion

        #region ICommunicationMonitor Members

        public StatusMonitorBase CommunicationMonitor { get; private set; }

        #endregion

        public ISelectableItems<string> Inputs { get; private set; }

        private void Init()
        {
            WarmupTime = warmingTimeMs > 0 ? warmingTimeMs : 10000;
            CooldownTime = coolingTimeMs > 0 ? coolingTimeMs : 8000;

            inputFeedback = new List<bool>();
            InputFeedback = new List<BoolFeedback>();

            if (upperLimit != lowerLimit && upperLimit > lowerLimit)
            {
                ScaleVolume = true;
            }

            var socket = Communication as ISocketStatus;
            if (socket != null)
            {
                //This Instance Uses IP Control
                if (UsesIpControlKeycode)
                {
                    SetupIpControl(socket);
                }
                else
                {
                    this.LogVerbose("The LG Display Plugin does NOT support IP Control currently");
                }
            }
            else
            {
                // This instance uses RS-232 Control
                isSerialComm = true;

                if (UsesIpControlKeycode)
                {
                    this.LogWarning("A keycode only applies to network control; ignoring it for this serial connection");
                    ipControlCipher = null;
                }
            }

            if (!UsesIpControlKeycode)
            {
                PortGather = new CommunicationGather(Communication, "x");
                PortGather.LineReceived += PortGather_LineReceived;
            }

            var pollInterval = pollIntervalMs > 0 ? pollIntervalMs : 10000;
            CommunicationMonitor = new GenericCommunicationMonitor(this, Communication, pollInterval, 180000, 300000,
                StatusGet);
            CommunicationMonitor.StatusChange += CommunicationMonitor_StatusChange;

            DeviceManager.AddDevice(CommunicationMonitor);

            if (!ScaleVolume)
            {
                volumeIncrementer = new ActionIncrementer(655, 0, 65535, 800, 80,
                    v => SetVolume((ushort)v),
                    () => lastVolumeSent);
            }
            else
            {
                var scaleUpper = NumericalHelpers.Scale(upperLimit, 0, 100, 0, 65535);
                var scaleLower = NumericalHelpers.Scale(lowerLimit, 0, 100, 0, 65535);

                volumeIncrementer = new ActionIncrementer(655, (int)scaleLower, (int)scaleUpper, 800, 80,
                    v => SetVolume((ushort)v),
                    () => lastVolumeSent);
            }

            MuteFeedback = new BoolFeedback("muteOn", () => IsMuted);
            VolumeLevelFeedback = new IntFeedback("volume", () => volumeLevelForSig);
            VideoMuteIsOn = new BoolFeedback("videoMuteOn", () => VideoIsMuted);

            // The base display list already holds power, input and warming/cooling. Add the rest of
            // the display's state so anything reading Feedbacks sees it; each fires when its value
            // changes.
            Feedbacks.Add(MuteFeedback);
            Feedbacks.Add(VolumeLevelFeedback);
            Feedbacks.Add(VideoMuteIsOn);
            Feedbacks.Add(InputNumberFeedback);

            AddRoutingInputPort(
                new RoutingInputPort(RoutingPortNames.HdmiIn1, eRoutingSignalType.Audio | eRoutingSignalType.Video,
                    eRoutingPortConnectionType.Hdmi, new Action(InputHdmi1), this), "90");
            AddRoutingInputPort(
                new RoutingInputPort(RoutingPortNames.HdmiIn2, eRoutingSignalType.Audio | eRoutingSignalType.Video,
                    eRoutingPortConnectionType.Hdmi, new Action(InputHdmi2), this), "91");
            AddRoutingInputPort(
                new RoutingInputPort(RoutingPortNames.HdmiIn3, eRoutingSignalType.Audio | eRoutingSignalType.Video,
                    eRoutingPortConnectionType.Hdmi, new Action(InputHdmi3), this), "92");
            AddRoutingInputPort(
                new RoutingInputPort(RoutingPortNames.HdmiIn4, eRoutingSignalType.Audio | eRoutingSignalType.Video,
                    eRoutingPortConnectionType.Hdmi, new Action(InputHdmi4), this), "93");
            AddRoutingInputPort(
                new RoutingInputPort(RoutingPortNames.DisplayPortIn, eRoutingSignalType.Audio | eRoutingSignalType.Video,
                    eRoutingPortConnectionType.DisplayPort, new Action(InputDisplayPort1), this), "c0");

            inputFeedback = new List<bool>(new bool[InputPorts.Count + 1]);

            // Initialize InputFeedback with a BoolFeedback for each input
            InputFeedback = new List<BoolFeedback>();
            for (int i = 0; i < InputPorts.Count; i++)
            {
                int index = i + 1; // 1-based index to match InputNumber logic
                InputFeedback.Add(new BoolFeedback(() => inputFeedback[index]));
            }

            SetupInputs();
        }

        protected override bool CustomActivate()
        {
            Communication.Connect();

            if (isSerialComm || overrideWol || HasWakeOnLanConfiguration || UsesIpControlKeycode)
            {
                CommunicationMonitor.Start();
            }

            return base.CustomActivate();
        }

        private bool UsesIpControlKeycode => ipControlCipher != null;

        private void SetupIpControl(ISocketStatus socket)
        {
            this.LogInformation("Using keycode-secured network IP control");

            ipControlCommands = new BlockingCollection<string>(IpControlQueueCapacity);

            ipControlCipher.ResponseReceived += response =>
            {
                string command;
                lock (ipControlLock)
                {
                    command = ipControlCommandInFlight;
                    ipControlCommandInFlight = null;
                }

                ipControlDecryptWarningLogged = false;
                receiveQueue.Enqueue(new ProcessStringMessage(response, r => ProcessIpControlResponse(command, r)));
                ipControlResponseReceived.Set();
            };

            ipControlCipher.UndecryptableResponse += () =>
            {
                if (ipControlDecryptWarningLogged) return;
                ipControlDecryptWarningLogged = true;
                this.LogWarning("Could not decrypt a response from the display; check that the keycode matches the one shown under IP Control Setup");
            };

            Communication.BytesReceived += (sender, args) => ipControlCipher.Receive(args.Bytes);
            socket.ConnectionChange += (sender, args) =>
            {
                ipControlCipher.Reset();

                // A display that drops off the network has gone to standby; it can only be woken by Wake-on-LAN.
                if (!args.Client.IsConnected && !IsWarmingUp)
                    PowerIsOn = false;
            };

            new Thread(ProcessIpControlCommands) { IsBackground = true, Name = Key + "-ipcontrol" }.Start();
        }

        private void SendIpControl(string command)
        {
            if (!ipControlCommands.TryAdd(command))
            {
                this.LogWarning("IP control command queue is full; dropping {Command}", command);
            }
        }

        // Volume ramps send a level every few tens of milliseconds; only the latest level is worth sending.
        private void SendIpControlVolume(int level)
        {
            var command = string.Format("{0} {1}", IpControlVolumePlaceholder, Math.Max(0, Math.Min(100, level)));
            if (Interlocked.Exchange(ref queuedIpControlVolumeCommand, command) == null)
            {
                SendIpControl(IpControlVolumePlaceholder);
            }
        }

        private void ProcessIpControlCommands()
        {
            foreach (var queued in ipControlCommands.GetConsumingEnumerable())
            {
                var command = queued == IpControlVolumePlaceholder
                    ? Interlocked.Exchange(ref queuedIpControlVolumeCommand, null)
                    : queued;

                if (command == null) continue;

                try
                {
                    if (!Communication.IsConnected)
                    {
                        this.LogVerbose("Not connected; skipping {Command}", command);
                        continue;
                    }

                    lock (ipControlLock)
                    {
                        ipControlCommandInFlight = command;
                    }

                    ipControlResponseReceived.Reset();
                    Communication.SendBytes(ipControlCipher.Encrypt(command));

                    if (!ipControlResponseReceived.WaitOne(IpControlResponseTimeoutMs))
                    {
                        lock (ipControlLock)
                        {
                            ipControlCommandInFlight = null;
                        }

                        this.LogVerbose("No response to {Command}", command);
                    }
                }
                catch (Exception e)
                {
                    this.LogError("Error sending {Command}: {Error}", command, e.Message);
                }
            }
        }

        private void ProcessIpControlResponse(string command, string response)
        {
            if (command == null)
            {
                this.LogVerbose("Unsolicited response: {Response}", response);
                return;
            }

            var verb = command.Split(' ')[0];
            switch (verb)
            {
                case "POWER_STATUS":
                    UpdateIpControlPowerFb(response);
                    break;
                case "CURRENT_APP":
                    UpdateIpControlAppFb(response);
                    break;
                case "CURRENT_VOL":
                    {
                        var match = Regex.Match(response, @"^VOL:(\d+)$");
                        if (match.Success)
                            ApplyVolumeFb(int.Parse(match.Groups[1].Value, CultureInfo.InvariantCulture));
                        else
                            this.LogVerbose("Unable to parse volume response: {Response}", response);
                        break;
                    }
                case "MUTE_STATE":
                    {
                        var match = Regex.Match(response, @"^MUTE:(on|off)$", RegexOptions.IgnoreCase);
                        if (match.Success)
                            IsMuted = match.Groups[1].Value.Equals("on", StringComparison.OrdinalIgnoreCase);
                        else
                            this.LogVerbose("Unable to parse mute response: {Response}", response);
                        break;
                    }
                case "VOLUME_MUTE":
                    if (response == "OK")
                        IsMuted = command.EndsWith(" on", StringComparison.Ordinal);
                    break;
                case "SCREEN_MUTE":
                    if (response == "OK")
                        VideoIsMuted = !command.EndsWith(" allmuteoff", StringComparison.Ordinal);
                    break;
                case "POWER":
                    // The display accepted power off, so show it off now rather than at the next poll.
                    if (response == "OK" && command == "POWER off")
                        PowerIsOn = false;
                    break;
            }

            if (response != "OK" && (verb == "POWER" || verb == "INPUT_SELECT" || verb == "VOLUME_CONTROL" || verb == "VOLUME_MUTE" || verb == "SCREEN_MUTE"))
            {
                this.LogWarning("Display rejected {Command}: {Response}", command, response);
            }
        }

        // POWER_STATUS answers "Power Status: active" when the display is on, and something else
        // (e.g. "always ready") in network standby, where the display keeps answering commands.
        private void UpdateIpControlPowerFb(string response)
        {
            var match = Regex.Match(response ?? string.Empty, @"Power\s*Status\s*:\s*(.+)$", RegexOptions.IgnoreCase);
            if (!match.Success)
            {
                if (!ipControlPowerStatusSupported)
                    this.LogVerbose("Unrecognized POWER_STATUS response, using CURRENT_APP for power: {Response}", response);
                return;
            }

            ipControlPowerStatusSupported = true;
            var status = match.Groups[1].Value.Trim();
            if (!string.Equals(status, lastIpControlPowerStatus, StringComparison.OrdinalIgnoreCase))
            {
                lastIpControlPowerStatus = status;
                this.LogInformation("Display reports power status '{Status}'", status);
            }

            PowerIsOn = status.StartsWith("active", StringComparison.OrdinalIgnoreCase);
        }

        // CURRENT_APP returns the foreground app, e.g. "APP:com.webos.app.hdmi2 Hot plug:Connected Signal:Yes ...".
        // A display in network standby can still report an app, so this only sets power until the
        // display has answered POWER_STATUS.
        private void UpdateIpControlAppFb(string response)
        {
            this.LogDebug("CURRENT_APP: {Response}", response);

            if (!ipControlPowerStatusSupported)
                PowerIsOn = !string.IsNullOrWhiteSpace(response);

            if (string.IsNullOrWhiteSpace(response))
                return;

            var match = Regex.Match(response, @"^APP:com\.webos\.app\.hdmi([1-4])\b", RegexOptions.IgnoreCase);
            if (match.Success)
            {
                var hdmi = int.Parse(match.Groups[1].Value, CultureInfo.InvariantCulture);
                UpdateInputFb((0x90 + hdmi - 1).ToString("x2"));
            }
        }

        private void SetupInputs()
        {
            Inputs = new LgInputs
            {
                Items = new Dictionary<string, ISelectableItem>
                {
                    { "90", new LgInput("90", "HDMI 1", this) },
                    { "91", new LgInput("91", "HDMI 2", this) },
                    { "92", new LgInput("92", "HDMI 3", this) },
                    { "93", new LgInput("93", "HDMI 4", this) },
                    { "c0", new LgInput("c0", "DisplayPort", this) },
                }
            };
            ApplyFriendlyNames(config);
        }
        private void ApplyFriendlyNames(LgDisplayPropertiesConfig config)
        {
            if (config?.FriendlyNames == null || Inputs == null || Inputs.Items == null)
                return;

            foreach (var friendly in config.FriendlyNames)
            {
                if (!string.IsNullOrEmpty(friendly.InputKey) && !string.IsNullOrEmpty(friendly.Name))
                {
                    if (friendly.HideInput)
                    {
                        // Remove the input if hideInput is true
                        Inputs.Items.Remove(friendly.InputKey);
                    }
                    else if (Inputs.Items.TryGetValue(friendly.InputKey, out var input))
                    {
                        // Create a new instance of the input with the updated name  
                        var updatedInput = new LgInput(input.Key, friendly.Name, this);
                        Inputs.Items[friendly.InputKey] = updatedInput;
                    }
                }
            }
        }

        private void CommunicationMonitor_StatusChange(object sender, MonitorStatusChangeEventArgs e)
        {
            CommunicationMonitor.IsOnlineFeedback.FireUpdate();

            if (e.Status == MonitorStatus.IsOk)
            {
                UpdateDeviceInfoIfMissing();
            }
        }

        private void PortGather_LineReceived(object sender, GenericCommMethodReceiveTextArgs args)
        {
            receiveQueue.Enqueue(new ProcessStringMessage(args.Text, ProcessResponse));
        }

        private void PollAfterNgResponse(string ngCommand)
        {
            switch (ngCommand)
            {
                case "a":
                    {
                        PowerGet();
                        break;
                    }
                case "b":
                    {
                        if (!PowerIsOn) return;

                        InputGet();
                        break;
                    }
                case "f":
                    {
                        if (!PowerIsOn) return;

                        VolumeGet();
                        break;
                    }
                case "e":
                    {
                        if (!PowerIsOn) return;

                        MuteGet();
                        break;
                    }
                case "d":
                    {
                        if (!PowerIsOn) return;

                        VideoMuteGet();
                        break;
                    }
            }
        }

        private void ProcessResponse(string s)
        {
            // Expected format: "{command} {id} {OK|NG}{value}x"
            // Example OK: "a 1 OK01x"  Example NG: "a 1 NG01x"
            var data = s.Trim().Split(' ');

            if (data.Length < 3)
            {
                this.LogVerbose("Unable to parse response, not enough data: {0}", s);
                return;
            }

            var command = data[0];
            var id = data[1];
            var statusAndValue = data[2];

            // Check for NG response
            if (statusAndValue.IndexOf("NG", StringComparison.OrdinalIgnoreCase) >= 0)
            {
                this.LogVerbose("NG response received for command '{0}': {1}", command, s);
                PollAfterNgResponse(command);
                return;
            }

            // Strip OK prefix and trailing x delimiter
            var responseValue = statusAndValue
                .Replace("OK", "")
                .TrimEnd('x');

            // Normalize both IDs to integers for comparison (handles "1" vs "01")
            var normalizedReceived = NormalizeId(id);
            var normalizedExpected = NormalizeId(Id);

            if (!normalizedReceived.Equals(normalizedExpected))
            {
                this.LogVerbose("Device ID Mismatch - Expected: {0} ({1}), Received: {2} ({3}) - Discarding",
                    Id, normalizedExpected, id, normalizedReceived);
                return;
            }

            switch (command)
            {
                case "a":
                    UpdatePowerFb(responseValue);
                    break;
                case "b":
                    UpdateInputFb(responseValue);
                    break;
                case "f":
                    UpdateVolumeFb(responseValue);
                    break;
                case "e":
                    UpdateMuteFb(responseValue);
                    break;
                case "d":
                    UpdateVideoMuteFb(responseValue);
                    break;
                case "y":
                    {
                        var serial = LgDeviceInfoReply.Value(s);
                        SetDeviceInfo(d => d.SerialNumber = serial, serial);
                        break;
                    }
                case "z":
                    {
                        var version = LgDeviceInfoReply.Value(s);
                        SetDeviceInfo(d => d.FirmwareVersion = version, version);
                        break;
                    }
            }
        }

        /// <summary>
        /// Normalizes device ID for comparison (converts to integer string to handle "01" vs "1")
        /// </summary>
        /// <param name="deviceId">Device ID to normalize</param>
        /// <returns>Normalized device ID as integer string</returns>
        private string NormalizeId(string deviceId)
        {
            try
            {
                // Convert to int and back to string to remove leading zeros
                return int.Parse(deviceId).ToString();
            }
            catch (Exception e)
            {
                this.LogError("Failed to normalize device ID '{DeviceId}': {Error}", deviceId, e.Message);
                return deviceId; // Return original if parsing fails
            }
        }

        private void AddRoutingInputPort(RoutingInputPort port, string fbMatch)
        {
            port.FeedbackMatchObject = fbMatch;
            InputPorts.Add(port);
        }

        /// <summary>
        /// Formats an outgoing message
        /// 
        /// </summary>
        /// <param name="s"></param>
        public void SendData(string s)
        {
            var commandPrefix = s.Length >= 2 ? s.Substring(0, 2) : string.Empty;

            if (lastCommandPrefix == "kf" && commandPrefix != "kf")
            {
                CrestronEnvironment.Sleep(100);
            }

            lastCommandPrefix = commandPrefix;

            Communication.SendText(s + "\x0D");
        }

        /// <summary>
        /// Sets the requested input
        /// </summary>
        private int SetInput
        {
            set
            {
                if (value <= 0 || value > InputPorts.Count)
                {
                    this.LogError("SetInput: Value {0} is out of range (1-{1})", value, InputPorts.Count);
                    return;
                }

                var portIndex = value - 1;

                var port = GetInputPort(portIndex);
                if (port == null)
                {
                    this.LogError("SetInput: Port at index {0} is null", portIndex);
                    return;
                }

                if (port.Selector is Action action)
                {
                    this.LogVerbose("SetInput: {0}", action?.Method.DeclaringType?.Name + "." + action?.Method.Name ?? "NULL");
                    ExecuteSwitch(action);
                }
                else
                {
                    this.LogError("SetInput: Port selector is not an Action! Type: {0}",
                        port.Selector?.GetType().Name ?? "NULL");
                }
            }
        }

        private RoutingInputPort GetInputPort(int input)
        {
            return InputPorts.ElementAt(input);
        }

        /// <summary>
        /// Lists available input routing ports
        /// </summary>
        public void ListRoutingInputPorts()
        {
            foreach (var inputPort in InputPorts)
            {
                this.LogVerbose("ListRoutingInputPorts: key-'{0}', connectionType-'{1}', feedbackMatchObject-'{2}'",
                    inputPort.Key, inputPort.ConnectionType, inputPort.FeedbackMatchObject);
            }
        }

        /// <summary>
        /// Poll Mute State
        /// </summary>
        public void MuteGet()
        {
            if (UsesIpControlKeycode)
            {
                SendIpControl("MUTE_STATE");
                return;
            }

            SendData(string.Format("ke {0} FF", Id));
        }

        /// <summary>
        /// Poll Volume
        /// </summary>
        public void VolumeGet()
        {
            if (UsesIpControlKeycode)
            {
                SendIpControl("CURRENT_VOL");
                return;
            }

            SendData(string.Format("kf {0} FF", Id));
        }

        /// <summary>
        /// Select Hdmi 1 Input
        /// </summary>
        public void InputHdmi1()
        {
            if (UsesIpControlKeycode)
            {
                SendIpControl("INPUT_SELECT hdmi1");
                return;
            }

            SendData(string.Format("xb {0} 90", Id));
        }

        /// <summary>
        /// Select Hdmi 2 Input
        /// </summary>
        public void InputHdmi2()
        {
            if (UsesIpControlKeycode)
            {
                SendIpControl("INPUT_SELECT hdmi2");
                return;
            }

            SendData(string.Format("xb {0} 91", Id));
        }

        /// <summary>
        /// Select Hdmi 3 Input
        /// </summary>
        public void InputHdmi3()
        {
            if (UsesIpControlKeycode)
            {
                SendIpControl("INPUT_SELECT hdmi3");
                return;
            }

            SendData(string.Format("xb {0} 92", Id));
        }

        /// <summary>
        /// Select Hdmi 4 Input
        /// </summary>
        public void InputHdmi4()
        {
            if (UsesIpControlKeycode)
            {
                SendIpControl("INPUT_SELECT hdmi4");
                return;
            }

            SendData(string.Format("xb {0} 93", Id));
        }

        /// <summary>
        /// Select DisplayPort Input
        /// </summary>
        public void InputDisplayPort1()
        {
            if (UsesIpControlKeycode)
            {
                this.LogWarning("Keycode IP control has no DisplayPort input");
                return;
            }

            SendData(string.Format("xb {0} C0", Id));
        }

        /// <summary>
        /// Poll input
        /// </summary>
        public void InputGet()
        {
            if (UsesIpControlKeycode)
            {
                SendIpControl("CURRENT_APP");
                return;
            }

            SendData(string.Format("xb {0} FF", Id));
        }

        /// <summary>
        /// Executes a switch, turning on display if necessary.
        /// </summary>
        /// <param name="selector"></param>
        public override void ExecuteSwitch(object selector)
        {
            if (!(selector is Action action))
            {
                this.LogVerbose("ExecuteSwitch: selector is not an Action. Type: {0}", selector?.GetType().Name ?? "NULL");
                return;
            }

            this.LogVerbose("ExecuteSwitch: preparing to execute {0}", action?.Method.DeclaringType?.Name + "." + action?.Method.Name ?? "NULL");

            inputSwitchPending = true;

            if (PowerIsOn)
            {
                try
                {
                    action();
                }
                finally
                {
                    inputSwitchPending = false;
                }
            }
            else if (IsCoolingDown)
            {
                this.LogVerbose("ExecuteSwitch: Device is cooling down. Powering on and executing {0} after cooldown.", action?.Method.DeclaringType?.Name + "." + action?.Method.Name ?? "NULL");
                CrestronInvoke.BeginInvoke((o) =>
                {
                    try
                    {
                        CrestronEnvironment.Sleep((int)CooldownTime);

                        this.LogVerbose("ExecuteSwitch: Cooldown complete. Sending power on.");
                        SendPowerOn();

                        CrestronEnvironment.Sleep((int)WarmupTime + 1000); // warmup time + 1000 for input switching delay                    

                        this.LogVerbose("ExecuteSwitch: Warmup complete. Executing {0}.", action?.Method.DeclaringType?.Name + "." + action?.Method.Name ?? "NULL");
                        action();
                    }
                    finally
                    {
                        inputSwitchPending = false;
                    }
                });
            }
            else
            {
                this.LogVerbose("ExecuteSwitch: Power is off. Powering on and executing {0} after warmup.", action?.Method.DeclaringType?.Name + "." + action?.Method.Name ?? "NULL");
                PowerOn();
                CrestronInvoke.BeginInvoke((o) =>
                {
                    try
                    {
                        CrestronEnvironment.Sleep((int)WarmupTime + 1000); // warmup time + 1000 for input switching delay                    

                        this.LogVerbose("ExecuteSwitch: Warmup complete. Executing {0}.", action?.Method.DeclaringType?.Name + "." + action?.Method.Name ?? "NULL");
                        action();
                    }
                    finally
                    {
                        inputSwitchPending = false;
                    }
                });
            }
        }


        /// <summary>
        /// Set Power On For Device
        /// </summary>
        public override void PowerOn()
        {
            if (IsCoolingDown)
            {
                if (powerOnPending) return;

                powerOnPending = true;
                CrestronInvoke.BeginInvoke((o) =>
                {
                    try
                    {
                        this.LogVerbose("PowerOn: Device is cooling down. Powering on after cooldown completes.");

                        CrestronEnvironment.Sleep((int)CooldownTime);

                        this.LogVerbose("PowerOn: Cooldown complete. Powering on.");
                        SendPowerOn();
                    }
                    finally
                    {
                        powerOnPending = false;
                    }
                });
                return;
            }

            SendPowerOn();
        }

        /// <summary>
        /// Set Power Off for Device
        /// </summary>
        public override void PowerOff()
        {
            if (IsWarmingUp)
            {
                CrestronInvoke.BeginInvoke((o) =>
                {
                    this.LogVerbose("PowerOff: Device is warming up. Powering off after warmup completes.");

                    CrestronEnvironment.Sleep((int)WarmupTime);

                    this.LogVerbose("PowerOff: Warmup complete. Powering off.");
                    SendPowerOff();
                });
                return;
            }

            SendPowerOff();
        }

        private void SendPowerOn()
        {
            var powerCommandSent = false;

            SendWakeOnLan();

            if (UsesIpControlKeycode)
            {
                // Keycode IP control cannot power a display on; Wake-on-LAN is the only way.
                powerCommandSent = HasWakeOnLanConfiguration;
            }
            else if (isSerialComm || overrideWol)
            {
                SendData(string.Format("ka {0} {1}", Id, smallDisplay ? "1" : "01"));
                powerCommandSent = true;
            }

            if (powerCommandSent && !PowerIsOn)
            {
                IsCoolingDown = false;
                IsWarmingUp = true;
            }
        }

        private void SendPowerOff()
        {
            var wasPowerOn = PowerIsOn;

            if (UsesIpControlKeycode)
                SendIpControl("POWER off");
            else
                SendData(string.Format("ka {0} {1}", Id, smallDisplay ? "0" : "00"));

            IsWarmingUp = false;

            if (wasPowerOn)
            {
                IsCoolingDown = true;
            }
        }

        /// <summary>
        /// Poll Power
        /// </summary>
        public void PowerGet()
        {
            if (UsesIpControlKeycode)
            {
                SendIpControl("POWER_STATUS");
                return;
            }

            SendData(string.Format("ka {0} FF", Id));
        }


        /// <summary>
        /// Toggle current power state for device
        /// </summary>
        public override void PowerToggle()
        {
            if (PowerIsOn)
            {
                PowerOff();
            }
            else
            {
                PowerOn();
            }
        }

        /// <summary>
        /// Process Input Feedback from Response
        /// </summary>
        /// <param name="s">response from device</param>
        public void UpdateInputFb(string s)
        {
            var normalizedInput = s.ToLower();

            var newInput = InputPorts.FirstOrDefault(i => i.FeedbackMatchObject.Equals(normalizedInput));
            if (newInput != null && newInput != currentInputPort)
            {
                currentInputPort = newInput;
                CurrentInputFeedback.FireUpdate();
                InputNumber = InputPorts.ToList().IndexOf(newInput) + 1;
            }

            if (Inputs.Items.ContainsKey(normalizedInput))
            {
                foreach (var item in Inputs.Items)
                {
                    item.Value.IsSelected = item.Key.Equals(normalizedInput);
                }

                Inputs.CurrentItem = normalizedInput;
            }
        }

        /// <summary>
        /// Process Power Feedback from Response
        /// </summary>
        /// <param name="s">response from device</param>
        public void UpdatePowerFb(string s)
        {
            var wasOn = PowerIsOn;
            PowerIsOn = s.Contains("1");
        }

        /// <summary>
        /// Process Video Mute Feedback from Response
        /// </summary>
        /// <param name="s">response from device</param>
        public void UpdateVideoMuteFb(string s)
        {
            try
            {
                var state = Convert.ToInt32(s);

                if (state == 0)
                {
                    VideoIsMuted = false;
                }
                else if (state == 1)
                {
                    VideoIsMuted = true;
                }
            }
            catch (Exception e)
            {
                this.LogVerbose("Unable to parse {Value} to Int32 {Error}", s, e);
            }
        }

        /// <summary>
        /// Process Volume Feedback from Response
        /// </summary>
        /// <param name="s">response from device</param>
        public void UpdateVolumeFb(string s)
        {
            try
            {
                ApplyVolumeFb(int.Parse(s, NumberStyles.HexNumber));
            }
            catch (Exception e)
            {
                this.LogVerbose("Error updating volumefb for value: {Value}: {Error}", s, e);
            }
        }

        /// <summary>
        /// Updates volume feedback from a display volume of 0-100
        /// </summary>
        /// <param name="vol">display volume</param>
        private void ApplyVolumeFb(int vol)
        {
            try
            {
                ushort newVol;
                if (!ScaleVolume)
                {
                    newVol = (ushort)NumericalHelpers.Scale(Convert.ToDouble(vol), 0, 100, 0, 65535);
                }
                else
                {
                    newVol = (ushort)NumericalHelpers.Scale(Convert.ToDouble(vol), lowerLimit, upperLimit, 0, 65535);
                }
                if (!volumeIsRamping)
                {
                    lastVolumeSent = newVol;
                }

                if (newVol == volumeLevelForSig)
                {
                    return;
                }
                volumeLevelForSig = newVol;
                VolumeLevelFeedback.FireUpdate();
            }
            catch (Exception e)
            {
                this.LogVerbose("Error updating volumefb for value: {Value}: {Error}", vol, e);
            }
        }

        /// <summary>
        /// Process Mute Feedback from Response
        /// </summary>
        /// <param name="s">response from device</param>
        public void UpdateMuteFb(string s)
        {
            try
            {
                var state = Convert.ToInt32(s);

                if (state == 0)
                {
                    IsMuted = true;
                }
                else if (state == 1)
                {
                    IsMuted = false;
                }
            }
            catch (Exception e)
            {
                this.LogVerbose("Unable to parse {Value} to Int32 {Error}", s, e);
            }
        }

        /// <summary>
        /// Updates Digital Route Feedback for Simpl EISC
        /// </summary>
        /// <param name="data">currently routed source</param>
        private void UpdateBooleanFeedback(int data)
        {
            try
            {
                if (data < 0 || data >= inputFeedback.Count)
                {
                    this.LogVerbose("Input index {Index} out of range for _inputFeedback (size {Size})", data, inputFeedback.Count);
                    return;
                }

                if (inputFeedback[data])
                {
                    return;
                }

                for (var i = 1; i < InputPorts.Count + 1; i++)
                {
                    inputFeedback[i] = false;
                }

                inputFeedback[data] = true;
                foreach (var item in InputFeedback)
                {
                    var update = item;
                    update.FireUpdate();
                }
            }
            catch (Exception e)
            {
                this.LogError("{Error}", e.Message);
            }
        }

        /// <summary>
        /// Starts the Poll Ring
        /// </summary>
        public void StatusGet()
        {
            if (UsesIpControlKeycode)
            {
                // Commands are already sent one at a time, so no spacing is needed between polls.
                PowerGet();

                // Until the display has answered POWER_STATUS, CURRENT_APP stands in for power.
                if (!ipControlPowerStatusSupported)
                    InputGet();

                if (!PowerIsOn || IsWarmingUp || IsCoolingDown)
                    return;

                if (ipControlPowerStatusSupported)
                    InputGet();
                VolumeGet();
                MuteGet();
                return;
            }

            //SendBytes(new byte[] { Header, StatusControlCmd, 0x00, 0x00, StatusControlGet, 0x00 });
            CrestronInvoke.BeginInvoke((o) =>
                {
                    PowerGet();

                    if (IsWarmingUp || IsCoolingDown)
                        return;

                    CrestronEnvironment.Sleep(1500);
                    InputGet();
                    CrestronEnvironment.Sleep(1500);
                    VolumeGet();
                    CrestronEnvironment.Sleep(1500);
                    MuteGet();
                });
        }


        private bool HasWakeOnLanConfiguration =>
            !string.IsNullOrWhiteSpace(wolMacAddress) &&
            !string.IsNullOrWhiteSpace(wolBroadcastAddress);

        private void SendWakeOnLan()
        {
            if (!HasWakeOnLanConfiguration)
            {
                this.LogWarning("Wake-on-LAN skipped: macAddress configured={HasMacAddress}, wolBroadcastAddress configured={HasBroadcastAddress}",
                    !string.IsNullOrWhiteSpace(wolMacAddress), !string.IsNullOrWhiteSpace(wolBroadcastAddress));
                return;
            }

            var normalizedMacAddress = Regex.Replace(wolMacAddress, @"[-:.]", string.Empty);
            if (!Regex.IsMatch(normalizedMacAddress, @"^[0-9A-Fa-f]{12}$"))
            {
                this.LogWarning("Wake-on-LAN is configured with an invalid MAC address: {MacAddress}", wolMacAddress);
                return;
            }

            var macAddressBytes = Enumerable.Range(0, 6)
                .Select(index => byte.Parse(normalizedMacAddress.Substring(index * 2, 2), NumberStyles.HexNumber))
                .ToArray();
            var magicPacket = new byte[102];

            for (var index = 0; index < 6; index++)
                magicPacket[index] = 0xFF;

            for (var index = 6; index < magicPacket.Length; index += macAddressBytes.Length)
                Buffer.BlockCopy(macAddressBytes, 0, magicPacket, index, macAddressBytes.Length);

            wolServer ??= new GenericUdpServer(Key + "-wol", wolBroadcastAddress, wolPort, magicPacket.Length);
            if (!wolServer.IsConnected)
                wolServer.Connect();

            if (!wolServer.IsConnected)
            {
                this.LogError("Wake-on-LAN UDP socket could not connect to {BroadcastAddress}:{Port}", wolBroadcastAddress, wolPort);
                return;
            }

            for (var attempt = 0; attempt < 3; attempt++)
                wolServer.SendBytes(magicPacket);

            this.LogInformation("Wake-on-LAN magic packets sent to {BroadcastAddress}:{Port}", wolBroadcastAddress, wolPort);
        }
    }
}
