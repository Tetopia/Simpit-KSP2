﻿using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Reflection;
using System.Runtime.InteropServices;
using KSP.IO;
using KSP.Sim;
using KSP.Sim.impl;
using Simpit;
using SpaceWarp.API.Game;
using SpaceWarp.API.Game.Extensions;
using UnityEngine;
using static RTG.CameraFocus;

namespace Simpit.Providers
{
    public class KerbalSimpitCAGProvider : MonoBehaviour
    {
        [StructLayout(LayoutKind.Sequential, Pack = 1)]
        [Serializable]
        public class CAGStatusStruct
        {
            [MarshalAs(UnmanagedType.ByValArray, SizeConst = 32)]
            public byte[] status;

            public CAGStatusStruct()
            {
                status = new byte[32];
                //Initialize all values to all at the begining
                for (int i = 0; i < 32; i++)
                {
                    status[i] = 0;
                }
            }

            public bool Equals(CAGStatusStruct obj)
            {
                if (status.Length != obj.status.Length)
                {
                    return false;
                }
                for (int i = 0; i < status.Length; i++)
                {
                    if (status[i] != obj.status[i])
                    {
                        return false;
                    }
                }
                return true;
            }
        }

        private EventDataObsolete<byte, object> enableChannel, disableChannel,
            toggleChannel, CAGSetSingleChannel;
        //Using a queue here in case the controller rapidly sets multiple action groups in succession
        //to avoid the buffer being overwritten before it can be applied to the game
        private volatile ConcurrentQueue<byte> setSingleBuffer = new ConcurrentQueue<byte>();

        // Outbound messages
        private EventDataObsolete<byte, object> CAGStateChannel, AdvancedCAGStateChannel;

        //private static bool AGXPresent;
        //private static Type AGXExternal;

        private CAGStatusStruct lastCAGStatus;
        private UInt32 lastAdvancedCAGStatus;

        // If set to true, the state should be sent at the next update even if no changes
        // are detected (for instance to initialise it after a new registration).
        private bool resendState, resendAdvancedState = false;

        private static KSPActionGroup[] ActionGroupIDs = new KSPActionGroup[] {
            KSPActionGroup.None,
            KSPActionGroup.Custom01,
            KSPActionGroup.Custom02,
            KSPActionGroup.Custom03,
            KSPActionGroup.Custom04,
            KSPActionGroup.Custom05,
            KSPActionGroup.Custom06,
            KSPActionGroup.Custom07,
            KSPActionGroup.Custom08,
            KSPActionGroup.Custom09,
            KSPActionGroup.Custom10
        };

        public void Start()
        {
            //AGXPresent = AGXInstalled();
            //if (SimpitPlugin.Instance.config_verbose) SimpitPlugin.Instance.loggingQueueInfo.Enqueue(String.Format("ActionGroupsExtended installed: {0}", AGXPresent));

            enableChannel = GameEvents.FindEvent<EventDataObsolete<byte, object>>("onSerialReceived" + InboundPackets.CAGEnable);
            if (enableChannel != null) enableChannel.Add(enableCAGCallback);
            disableChannel = GameEvents.FindEvent<EventDataObsolete<byte, object>>("onSerialReceived" + InboundPackets.CAGDisable);
            if (disableChannel != null) disableChannel.Add(disableCAGCallback);
            toggleChannel = GameEvents.FindEvent<EventDataObsolete<byte, object>>("onSerialReceived" + InboundPackets.CAGToggle);
            if (toggleChannel != null) toggleChannel.Add(toggleCAGCallback);
            CAGSetSingleChannel = GameEvents.FindEvent<EventDataObsolete<byte, object>>("onSerialReceived" + +InboundPackets.SetSingleCAG);
            if (CAGSetSingleChannel != null) CAGSetSingleChannel.Add(actionSetSingleCallback);

            CAGStateChannel = GameEvents.FindEvent<EventDataObsolete<byte, object>>("toSerial" + OutboundPackets.CustomActionGroups);
            GameEvents.FindEvent<EventDataObsolete<byte, object>>("onSerialChannelForceSend" + OutboundPackets.CustomActionGroups).Add(resendActionGroup);
            AdvancedCAGStateChannel = GameEvents.FindEvent<EventDataObsolete<byte, object>>("toSerial" + OutboundPackets.AdvancedCustomActionGroups);
            GameEvents.FindEvent<EventDataObsolete<byte, object>>("onSerialChannelForceSend" + OutboundPackets.AdvancedCustomActionGroups).Add(resendAdvancedActionGroup);

            lastCAGStatus = new CAGStatusStruct();
        }

        public void OnDestroy()
        {
            if (enableChannel != null) enableChannel.Remove(enableCAGCallback);
            if (disableChannel != null) disableChannel.Remove(disableCAGCallback);
            if (toggleChannel != null) toggleChannel.Remove(toggleCAGCallback);
            if (CAGSetSingleChannel != null) CAGSetSingleChannel.Remove(actionSetSingleCallback);
        }

        private bool UpdateCurrentState()
        {
            CAGStatusStruct newState = getCAGState();
            if (!newState.Equals(lastCAGStatus) || resendState)
            {
                resendState = false;
                if (CAGStateChannel != null)
                {
                    //SimpitPlugin.Instance.loggingQueueInfo.Enqueue(String.Format("Sending CAG status : (" + newState.status[0] + ") (" + newState.status[1] + ") "));
                    CAGStateChannel.Fire(OutboundPackets.CustomActionGroups, newState);
                    lastCAGStatus = newState;
                }
                return true;
            }
            else
            {
                return false;
            }
        }

        private bool UpdateAdvancedCurrentState()
        {
            UInt32 newState = getAdvancedCAGState();
            if (!newState.Equals(lastAdvancedCAGStatus) || resendAdvancedState)
            {
                resendAdvancedState = false;
                if (AdvancedCAGStateChannel != null)
                {
                    AdvancedCAGStateChannel.Fire(OutboundPackets.AdvancedCustomActionGroups, newState);
                    lastAdvancedCAGStatus = newState;
                }
                return true;
            }
            else
            {
                return false;
            }
        }

        public void resendActionGroup(byte ID, object Data)
        {
            resendState = true;
        }

        public void resendAdvancedActionGroup(byte ID, object Data)
        {
            resendAdvancedState = true;
        }

        public void Update()
        {
            UpdateCurrentState();
            UpdateAdvancedCurrentState();

            byte actionGroupAndSetting;
            while (!setSingleBuffer.IsEmpty && setSingleBuffer.TryDequeue(out actionGroupAndSetting))
            {
                setSingleGroup(actionGroupAndSetting);
            }
        }

        /*
        public static bool AGXInstalled()
        {
            try
            {
                AGXExternal = Type.GetType("ActionGroupsExtended.AGExtExternal, AGExt");
                return (bool)AGXExternal.InvokeMember("AGXInstalled",
                                                      BindingFlags.InvokeMethod | BindingFlags.Public | BindingFlags.Static,
                                                      null, null, null);
            }
            catch
            {
                return false;
            }
        }

        private static bool AGXActivateGroupDelayCheck(int group, bool forceDir)
        {
            if (AGXPresent)
            {
                return (bool)AGXExternal.InvokeMember("AGXActivateGroupDelayCheck",
                                                      BindingFlags.InvokeMethod | BindingFlags.Public | BindingFlags.Static,
                                                      null, null, new System.Object[] { group, forceDir });
            }
            else
            {
                return false;
            }
        }

        private static bool AGXToggleGroupDelayCheck(int group)
        {
            if (AGXPresent)
            {
                return (bool)AGXExternal.InvokeMember("AGXToggleGroupDelayCheck",
                                                      BindingFlags.InvokeMethod | BindingFlags.Public | BindingFlags.Static,
                                                      null, null, new System.Object[] { group });
            }
            else
            {
                return false;
            }
        }
        */

        public void enableCAGCallback(byte ID, object Data)
        {
            VesselComponent simVessel = null;
            try { simVessel = Vehicle.ActiveSimVessel; } catch { }
            if (simVessel == null) return;

            byte[] groupIDs = (byte[])Data;
            int idx;
            for (int i = groupIDs.Length - 1; i >= 0; i--)
            {
                idx = (int)groupIDs[i];
                //if (AGXPresent) UnityMainThreadDispatcher.Instance().Enqueue(() => AGXActivateGroupDelayCheck(idx, true));
                //else 
                simVessel.SetActionGroup(ActionGroupIDs[idx], true);
            }
        }

        public void disableCAGCallback(byte ID, object Data)
        {
            VesselComponent simVessel = null;
            try { simVessel = Vehicle.ActiveSimVessel; } catch { }
            if (simVessel == null) return;

            byte[] groupIDs = (byte[])Data;
            int idx;
            for (int i = groupIDs.Length - 1; i >= 0; i--)
            {
                idx = (int)groupIDs[i];
                //if (AGXPresent) UnityMainThreadDispatcher.Instance().Enqueue(() => AGXActivateGroupDelayCheck(idx, false));
                //else 
                simVessel.SetActionGroup(ActionGroupIDs[idx], false);
            }
        }

        public void toggleCAGCallback(byte ID, object Data)
        {
            VesselComponent simVessel = null;
            try { simVessel = Vehicle.ActiveSimVessel; } catch { }
            if (simVessel == null) return;

            byte[] groupIDs = (byte[])Data;
            int idx;
            for (int i = groupIDs.Length - 1; i >= 0; i--)
            {
                idx = (int)groupIDs[i];
                //if (AGXPresent) UnityMainThreadDispatcher.Instance().Enqueue(() => AGXActivateGroupDelayCheck(idx, false));
                //else 
                simVessel.TriggerActionGroup(ActionGroupIDs[idx]);
            }
        }

        public void actionSetSingleCallback(byte ID, object Data)
        {
            byte[] payload = (byte[])Data;
            setSingleBuffer.Enqueue(payload[0]);
        }

        private void setSingleGroup(byte actionGroupAndSetting)
        {
            VesselComponent simVessel = null;
            try { simVessel = Vehicle.ActiveSimVessel; } catch { }
            if (simVessel == null) return;

            //Bitmask the first six bits as which action group to use and the last two bits as what to do with said action group
            int actionGroupIndex = (actionGroupAndSetting & 0b11111100) >> 2;
            KSPActionGroup actionGroup = ActionGroupIDs[actionGroupIndex];
            byte actionSetting = (byte)(actionGroupAndSetting & 0b00000011);

            bool activate = (actionSetting == ActionGroupSettings.activate);
            bool deactivate = (actionSetting == ActionGroupSettings.deactivate);
            bool toggle = (actionSetting == ActionGroupSettings.toggle);

            string debugString;
            if (activate) debugString = "Activate action group ";
            else if (deactivate) debugString = "Deactivate action group ";
            else debugString = "Toggle action group ";
            debugString += actionGroup.ToString();
            if (SimpitPlugin.Instance.config_verbose) SimpitPlugin.Instance.loggingQueueInfo.Enqueue(debugString);

            if (activate) simVessel.SetActionGroup(actionGroup, true);
            else if (deactivate) simVessel.SetActionGroup(actionGroup, false);
            else simVessel.TriggerActionGroup(actionGroup);
        }


        private CAGStatusStruct getCAGState()
        {
            CAGStatusStruct result = new CAGStatusStruct();

            VesselComponent simVessel = null;
            try { simVessel = Vehicle.ActiveSimVessel; } catch { }
            if (simVessel == null) return result;

            for (int i = 1; i < ActionGroupIDs.Length; i++) //Ignoring 0 since there is no Action Group 0
            {
                if (simVessel.GetActionGroupState(ActionGroupIDs[i]) == KSPActionGroupState.True)
                {
                    result.status[i / 8] |= (byte)(1 << (i % 8)); //Set the selected bit to 1
                }
            }

            /*
            if (AGXPresent)
            {
                for (int group = 11; group <= 250; group++) // Only call AGExt for additionnal actions
                {
                    bool activated = (bool)AGXExternal.InvokeMember("AGXGroupState",
                        BindingFlags.InvokeMethod | BindingFlags.Public | BindingFlags.Static, null, null, new System.Object[] { group });

                    if (activated)
                    {
                        result.status[group / 8] |= (byte)(1 << group % 8); //Set the selected bit to 1
                    }
                }
            }
            */
            return result;
        }

        private UInt32 getAdvancedCAGState()
        {
            VesselComponent simVessel = null;
            try
            {
                simVessel = Vehicle.ActiveSimVessel;
            }
            catch { return 0; }
            if (simVessel == null) return 0;

            UInt32 advancedGroups = 0;

            //Move the state of each action group to it's according place in the byte array

            for(int i = 1; i < ActionGroupIDs.Length; i++) //Ignoring 0 since there is no Action Group 0
            {
                UInt32 state = (UInt32)simVessel.GetActionGroupState(ActionGroupIDs[i]);
                int moveBy = i * 2; //move by 2 bits for each group
                advancedGroups |= state << moveBy;
            }

            return advancedGroups;
        }
    }
}