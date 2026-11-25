/* Copyright (c) 2024 Rick (rick 'at' gibbed 'dot' us)
 *
 * This software is provided 'as-is', without any express or implied
 * warranty. In no event will the authors be held liable for any damages
 * arising from the use of this software.
 *
 * Permission is granted to anyone to use this software for any purpose,
 * including commercial applications, and to alter it and redistribute it
 * freely, subject to the following restrictions:
 *
 * 1. The origin of this software must not be misrepresented; you must not
 *    claim that you wrote the original software. If you use this software
 *    in a product, an acknowledgment in the product documentation would
 *    be appreciated but is not required.
 *
 * 2. Altered source versions must be plainly marked as such, and must not
 *    be misrepresented as being the original software.
 *
 * 3. This notice may not be removed or altered from any source
 *    distribution.
 */

using System;
using System.Runtime.InteropServices;
using SAM.API.Interfaces;

namespace SAM.API.Wrappers
{
    public class SteamApps008 : NativeWrapper<ISteamApps008>
    {
        #region IsSubscribed
        [UnmanagedFunctionPointer(CallingConvention.ThisCall)]
        [return: MarshalAs(UnmanagedType.I1)]
        private delegate bool NativeIsSubscribedApp(IntPtr self, uint gameId);

        public bool IsSubscribedApp(uint gameId)
        {
            return this.Call<bool, NativeIsSubscribedApp>(this.Functions.IsSubscribedApp, this.ObjectAddress, gameId);
        }
        #endregion

        #region GetCurrentGameLanguage
        [UnmanagedFunctionPointer(CallingConvention.ThisCall)]
        private delegate IntPtr NativeGetCurrentGameLanguage(IntPtr self);

        public string GetCurrentGameLanguage()
        {
            var languagePointer = this.Call<IntPtr, NativeGetCurrentGameLanguage>(
                this.Functions.GetCurrentGameLanguage,
                this.ObjectAddress);
            return NativeStrings.PointerToString(languagePointer);
        }
        #endregion

        #region IsAppInstalled
        [UnmanagedFunctionPointer(CallingConvention.ThisCall)]
        [return: MarshalAs(UnmanagedType.I1)]
        private delegate bool NativeIsAppInstalled(IntPtr self, uint gameId);

        public bool IsAppInstalled(uint gameId)
        {
            return this.Call<bool, NativeIsAppInstalled>(
                this.Functions.IsAppInstalled,
                this.ObjectAddress,
                gameId);
        }
        #endregion

        #region GetAppOwner
        // 说明：Steam 实际返回的 ISteamApps 版本里这个槽位在部分客户端上不可调用（实测 AccessViolation），
        // 因此不封装。判断「这个 app 是谁的」改用 IsSubscribedFromFamilySharing + IsAppInstalled 组合。
        #endregion

        #region IsSubscribedFromFreeWeekend
        [UnmanagedFunctionPointer(CallingConvention.ThisCall)]
        [return: MarshalAs(UnmanagedType.I1)]
        private delegate bool NativeIsSubscribedFromFreeWeekend(IntPtr self, uint gameId);

        public bool IsSubscribedFromFreeWeekend(uint gameId)
        {
            return this.Call<bool, NativeIsSubscribedFromFreeWeekend>(
                this.Functions.IsSubscribedFromFreeWeekend,
                this.ObjectAddress,
                gameId);
        }
        #endregion

        #region IsSubscribedFromFamilySharing
        [UnmanagedFunctionPointer(CallingConvention.ThisCall)]
        [return: MarshalAs(UnmanagedType.I1)]
        private delegate bool NativeIsSubscribedFromFamilySharing(IntPtr self, uint gameId);

        public bool IsSubscribedFromFamilySharing(uint gameId)
        {
            return this.Call<bool, NativeIsSubscribedFromFamilySharing>(
                this.Functions.IsSubscribedFromFamilySharing,
                this.ObjectAddress,
                gameId);
        }
        #endregion
    }
}
