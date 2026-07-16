using System.Runtime.InteropServices;
using System;

namespace Tanks.Net;

public static unsafe class NativeUnix
{
    // ssize_t recvfrom(int sockfd, void *buf, size_t len, int flags,
    //                  struct sockaddr *src_addr, socklen_t *addrlen);
    [DllImport("libc", SetLastError = true, EntryPoint = "recvfrom")]
    public static extern nint RecvFrom(
        int sockfd,
        byte* buf,
        nuint len,
        int flags,
        byte* srcAddr,
        uint* addrLen);

    // ssize_t sendto(int sockfd, const void *buf, size_t len, int flags,
    //                const struct sockaddr *dest_addr, socklen_t addrlen);
    [DllImport("libc", SetLastError = true, EntryPoint = "sendto")]
    public static extern nint SendTo(
        int sockfd,
        byte* buf,
        nuint len,
        int flags,
        byte* destAddr,
        uint addrLen);

#if NOT_UNITY
    // On Unix, writing to a closed socket by default sends SIGPIPE to your process,
    // which terminates it. Unity's player installs a handler that ignores SIGPIPE
    // (via signal(SIGPIPE, SIG_IGN))
    //
    // sighandler_t signal(int signum, sighandler_t handler);
    // Returns previous handler, or SIG_ERR ((void*)-1) on error.
    [DllImport("libc", EntryPoint = "signal", SetLastError = true)]
    public static extern IntPtr Signal(int signum, IntPtr handler);

    public const int SIGPIPE = 13; // Linux + macOS + BSDs
    public static readonly IntPtr SIG_IGN = (IntPtr)1; // magic sentinel, portable
    public static readonly IntPtr SIG_ERR = (IntPtr)(-1);
#endif
}