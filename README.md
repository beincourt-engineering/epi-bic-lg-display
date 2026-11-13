# PepperDash Essentials LG Display Plugin (c) 2026

## License

Provided under MIT license

## Overview

This repo contains a plugin for use with [PepperDash Essentials](https://github.com/PepperDash/Essentials). This plugin enables Essentials to communicate with and control an LG display over RS-232.

## Example Config Object

```json
{
  "key": "display01",
  "name": "Display 1",
  "group": "displays",
  "type": "lg",
  "properties": {
    "id": "01",
    "volumeUpperLimit": 100,
    "volumeLowerLimit": 0,
    "pollIntervalMs": 45000,
    "coolingTime": 10000,
    "warmingTimeMs": 10000,
    "smallDisplay": false,
    "control": {
      "method": "com",
      "controlPortNumber": 1,
      "controlPortDevKey": "processor",
      "comParams": {
        "protocol": "RS232",
        "parity": "None",
        "baudRate": 9600,
        "dataBits": 8,
        "softwareHandshake": "None",
        "hardwareHandshake": "None",
        "stopBits": 1
      }
    }
    "friendlyNames": [                      //if you want to use friendly names, add this section
	        {
	        	"inputKey": "90",           //The input key for the input you want to use a friendly name for, this has to a valid input key(90,91,c0)
	        	"name": "Friendly Name 1",  //The desired name to be displayed on the screen
            "hideInput": false              //if set to true, the input will not be displayed in the list of inputs
	        },
	        {
	        	"inputKey": "91",
	        	"name": "Friendly Name 2",
            "hideInput": false
	        },
	        {
	        	"inputKey": "c0",
	        	"name": "Friendly Name 3",
            "hideInput": true

	        }
        ],
  }

}
```

The `smallDisplay` configuration option is used to control padding of the power on command. If `smallDisplay` is `true`, the power on command sent will be `ka 01 1`. If `smallDisplay` is `false`, the power on command sent will be `ka 01 01`.

### Network IP control with a keycode

webOS displays with an **IP Control Setup** menu accept network commands only when they are encrypted with the keycode that menu generates. Turn on **Network IP Control**, press **Generate Keycode**, and set `keycode` to the eight characters shown. Pressing **Generate Keycode** again replaces the keycode, so the config must be updated afterwards.

```json
"properties": {
  "keycode": "<keycode>",
  "macAddress": "<display-mac-address>",
  "wolBroadcastAddress": "<subnet-broadcast-address>",
  "control": {
    "method": "tcpIp",
    "tcpSshProperties": { "address": "<display-address>", "port": 9761, "autoReconnect": true }
  }
}
```

With a keycode set:

- Power off, HDMI 1-4 input selection, volume, volume mute and video mute use the display's network command set.
- Power on uses Wake-on-LAN only, so `macAddress` and `wolBroadcastAddress` are required and **Wake On LAN** must be on in the display menu.
- Polling reports power and the current HDMI input, volume and mute. Video mute feedback follows the commands sent, because the display has no query for it.
- `id` and `smallDisplay` are not used. DisplayPort is not available.

Without a keycode, a TCP connection uses the serial command set unchanged.

For more configuration information, see the [PepperDash Essentials wiki](https://github.com/PepperDash/Essentials/wiki).

## Github Actions

This repo contains two Github Action workflows that will build this project automatically. Modify the SOLUTION_PATH and SOLUTION_FILE environment variables as needed. Any branches named `feature/*`, `release/*`, `hotfix/*` or `development` will automatically be built with the action and create a release in the repository with a version number based on the latest release on the master branch. If there are no releases yet, the version number will be 0.0.1. The version number will be modified based on what branch triggered the build:

- `feature` branch builds will be tagged with an `alpha` descriptor, with the Action run appended: `0.0.1-alpha-1`
- `development` branch builds will be tagged with a `beta` descriptor, with the Action run appended: `0.0.1-beta-2`
- `release` branches will be tagged with an `rc` descriptor, with the Action run appended: `0.0.1-rc-3`
- `hotfix` branch builds will be tagged with a `hotfix` descriptor, with the Action run appended: `0.0.1-hotfix-4`

Builds on the `Main` branch will ONLY be triggered by manually creating a release using the web interface in the repository. They will be versioned with the tag that is created when the release is created. The tags MUST take the form `major.minor.revision` to be compatible with the build process. A tag like `v0.1.0-alpha` is NOT compatabile and may result in the build process failing.

If you have any questions about the action, contact Andrew Welker or Neil Dorin.
<!-- START Minimum Essentials Framework Versions -->
### Minimum Essentials Framework Versions

- 3.0.0
- 3.0.0
<!-- END Minimum Essentials Framework Versions -->
<!-- START Config Example -->
### Config Example

```json
{
    "key": "GeneratedKey",
    "uid": 1,
    "name": "GeneratedName",
    "type": "LgDisplayProperties",
    "group": "Group",
    "properties": {
        "id": "SampleString",
        "volumeUpperLimit": 0,
        "volumeLowerLimit": 0,
        "pollIntervalMs": 0,
        "coolingTimeMs": "SampleValue",
        "warmingTimeMs": "SampleValue",
        "udpSocketKey": "SampleString",
        "macAddress": "SampleString",
        "wolBroadcastAddress": "SampleString",
        "wolPort": "SampleValue",
        "smallDisplay": true,
        "overrideWol": true,
        "keycode": "SampleString",
        "friendlyNames": [
            {
                "inputKey": "SampleString",
                "name": "SampleString",
                "hideInput": true
            }
        ]
    }
}
```
<!-- END Config Example -->
<!-- START Supported Types -->

<!-- END Supported Types -->
<!-- START Join Maps -->

<!-- END Join Maps -->
<!-- START Interfaces Implemented -->
### Interfaces Implemented

- ISelectableItems<string>
- IBasicVolumeWithFeedback
- ICommunicationMonitor
- IBridgeAdvanced
- IHasInputs<string>
- IBasicVideoMuteWithFeedback
- IWarmingCooling
- IDeviceInfoProvider
- IBasicVolumeControls
<!-- END Interfaces Implemented -->
<!-- START Base Classes -->
### Base Classes

- DisplayControllerJoinMap
- TwoWayDisplayBase
- DisplayBase
- MessengerBase
<!-- END Base Classes -->
<!-- START Public Methods -->
### Public Methods

- public void Select()
- public void UpdateDeviceInfo()
- public void SetVolume(ushort level)
- public void MuteOn()
- public void MuteOff()
- public void MuteToggle()
- public void VolumeDown(bool pressRelease)
- public void VolumeUp(bool pressRelease)
- public void VideoMuteOn()
- public void VideoMuteOff()
- public void VideoMuteToggle()
- public void VideoMuteGet()
- public void LinkToApi(BasicTriList trilist, uint joinStart, string joinMapKey, EiscApiAdvanced bridge)
- public void SendData(string s)
- public void ListRoutingInputPorts()
- public void MuteGet()
- public void VolumeGet()
- public void InputHdmi1()
- public void InputHdmi2()
- public void InputHdmi3()
- public void InputHdmi4()
- public void InputDisplayPort1()
- public void InputGet()
- public void PowerGet()
- public void UpdateInputFb(string s)
- public void UpdatePowerFb(string s)
- public void UpdateVideoMuteFb(string s)
- public void UpdateVolumeFb(string s)
- public void UpdateMuteFb(string s)
- public void StatusGet()
- public string Decrypt(byte[] message)
- public void Receive(byte[] bytes)
- public void Reset()
- public void Select()
- public void LinkToApi(BasicTriList trilist, uint joinStart, string joinMapKey, EiscApiAdvanced bridge)
- public void SendIrCommand(string cmd)
- public void PowerOnPress(bool pressRelease)
- public void PowerOffPress(bool pressRelease)
- public void PowerTogglePress(bool pressRelease)
- public void ListRoutingInputPorts()
- public void InputHdmi1()
- public void InputHdmi1(bool pressRelease)
- public void InputHdmi2()
- public void InputHdmi2(bool pressRelease)
- public void InputHdmi3()
- public void InputHdmi3(bool pressRelease)
- public void InputHdmi4()
- public void InputHdmi4(bool pressRelease)
- public void InputTv()
- public void InputTv(bool pressRelease)
- public void InputAntenna()
- public void InputAntenna(bool pressRelease)
- public void InputNetflix()
- public void InputNetflix(bool pressRelease)
- public void InputPrimeVideo()
- public void InputPrimeVideo(bool pressRelease)
- public void InputToggle()
- public void InputToggle(bool pressRelease)
- public void VolumeUp(bool pressRelease)
- public void VolumeDown(bool pressRelease)
- public void MuteToggle()
- public void Feedback_Is_Keyed(string property, string type, string key)
- public void Keys_Are_Unique_And_Do_Not_Clash_With_The_Base_List()
- public void Feedback_Is_Added_To_The_List_During_Init(string property)
- public void Feedbacks_Are_Only_Added_During_Init()
- public void Init_Is_Called_From_The_Constructor()
- public void Display_Does_Not_Use_The_Base_Bridge_Link()
- public void Encrypts_The_Published_Example()
- public void Decrypts_The_Published_Example()
- public void Receives_A_Response_Split_Across_Reads(string response)
- public void Receives_Two_Responses_In_One_Read()
- public void Reports_Data_Encrypted_With_Another_Keycode()
- public void Rejects_An_Invalid_Keycode(string? keycode)
- public void Asks_the_configured_display()
- public void Reads_everything_after_OK(string reply, string expected)
- public void An_NG_or_empty_reply_has_no_value(string? reply)
- public void Formats_the_configured_mac(string? configured, string? expected)
- public void Config_Class_Exists()
- public void Config_Has_Parameterless_Constructor()
- public void Config_Property_Has_JsonPropertyAttribute(string jsonName)
- public void FriendlyName_Property_Has_JsonPropertyAttribute(string jsonName)
- public void Config_Property_Type_Matches(string propertyName, string expectedTypeName)
- public void FriendlyNames_Is_List_Of_FriendlyName()
- public void Config_Sample_Json_Has_Expected_Keys()
- public void FriendlyNames_Deserialize_As_List_With_Expected_Shape()
- public void Assembly_Loads_Successfully()
- public void Assembly_Name_Is_EpiDisplayLg()
- public void Factory_Count_Is_Two()
- public void Factory_Exists_ByName(string factoryClassName)
- public void All_Factories_Have_Parameterless_Constructor()
- public void Factory_Sets_MinimumEssentialsFrameworkVersion_To_3_0_0(string factoryClassName)
- public void Factory_Sets_TypeNames(string factoryClassName)
- public void Factory_Source_Contains_TypeName(string factoryClassName, string typeName)
- public void No_Duplicate_TypeNames_Across_Factories()
<!-- END Public Methods -->
<!-- START Bool Feedbacks -->
### Bool Feedbacks

- MuteFeedback
- VideoMuteIsOn
<!-- END Bool Feedbacks -->
<!-- START Int Feedbacks -->
### Int Feedbacks

- InputNumberFeedback
- VolumeLevelFeedback
<!-- END Int Feedbacks -->
<!-- START String Feedbacks -->

<!-- END String Feedbacks -->
