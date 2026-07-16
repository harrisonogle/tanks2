using System.Runtime.InteropServices;
using System;

namespace Tanks.Net;

public static unsafe class NativeWindows
{
    // int WSAAPI recvfrom(SOCKET s, char *buf, int len, int flags,
    //                     sockaddr *from, int *fromlen);
    [DllImport("ws2_32.dll", SetLastError = true, EntryPoint = "recvfrom")]
    public static extern int RecvFrom(
        IntPtr socket,
        byte* buf,
        int len,
        int flags,
        byte* fromAddr,
        int* fromLen);

    // int WSAAPI sendto(SOCKET s, const char *buf, int len, int flags,
    //                   const sockaddr *to, int tolen);
    [DllImport("ws2_32.dll", SetLastError = true, EntryPoint = "sendto")]
    public static extern int SendTo(
        IntPtr socket,
        byte* buf,
        int len,
        int flags,
        byte* toAddr,
        int toLen);

#if NOT_UNITY
    // Unity calls timeBeginPeriod(1) internally on Windows, so that
    // Windows timer resolution is 1ms instead of the default 15.6ms.
    // Allows for wait/sleep calls at 1ms resolution on Windows.
    //
    // Return value is MMRESULT - 0 (TIMERR_NOERROR) on success, 97 (TIMERR_NOCANDO)
    // if the requested period is out of range. Doesn't set Win32 last error, so no
    // SetLastError = true needed.

    // MMRESULT timeBeginPeriod(UINT uPeriod);
    [DllImport("winmm.dll", EntryPoint = "timeBeginPeriod")]
    public static extern uint TimeBeginPeriod(uint period);

    // MMRESULT timeEndPeriod(UINT uPeriod);
    [DllImport("winmm.dll", EntryPoint = "timeEndPeriod")]
    public static extern uint TimeEndPeriod(uint period);
#endif
}