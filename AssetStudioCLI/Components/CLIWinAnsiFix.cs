// Based on code by tomzorz (https://gist.github.com/tomzorz/6142d69852f831fb5393654c90a1f22e)
using System;
using System.Runtime.InteropServices;

namespace AssetStudioCLI
{
    static class CLIWinAnsiFix
    {
        public static readonly bool isAnsiSupported;
        private const int STD_OUTPUT_HANDLE = -11;
        private const uint ENABLE_VIRTUAL_TERMINAL_PROCESSING = 0x0004;

        [DllImport("kernel32.dll")]
        private static extern bool GetConsoleMode(IntPtr hConsoleHandle, out uint lpMode);

        [DllImport("kernel32.dll")]
        private static extern bool SetConsoleMode(IntPtr hConsoleHandle, uint dwMode);

        [DllImport("kernel32.dll")]
        private static extern IntPtr GetStdHandle(int nStdHandle);

        static CLIWinAnsiFix()
        { 
            bool isWin = RuntimeInformation.IsOSPlatform(OSPlatform.Windows);
            if (isWin)
            {
                isAnsiSupported = TryEnableVTMode();
                if (!isAnsiSupported)
                {
                    //Check for bash terminal emulator. E.g., Git Bash, Cmder
                    isAnsiSupported = Environment.GetEnvironmentVariable("TERM") != null;
                }
            }
            else
            {
                isAnsiSupported = true;
            }
        }

        // Enable support for ANSI escape codes
        // (but probably only suitable for windows 10+)
        // https://docs.microsoft.com/en-us/windows/console/console-virtual-terminal-sequences
        private static bool TryEnableVTMode()
        {
            var iStdOut = GetStdHandle(STD_OUTPUT_HANDLE);

            if (!GetConsoleMode(iStdOut, out uint outConsoleMode))
            {
                return false;
            }

            outConsoleMode |= ENABLE_VIRTUAL_TERMINAL_PROCESSING;

            if (!SetConsoleMode(iStdOut, outConsoleMode))
            {
                return false;
            }

            return true;
        }
    }
}
