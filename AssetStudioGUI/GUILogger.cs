using AssetStudio;
using System;
using System.Windows.Forms;

namespace AssetStudioGUI
{
    class GUILogger : ILogger
    {
        public bool ShowErrorMessage = false;
        private Action<string> action;

        public GUILogger(Action<string> action)
        {
            this.action = action;
        }

        public void Log(LoggerEvent loggerEvent, string message)
        {
            switch (loggerEvent)
            {
                case LoggerEvent.Error:
                    MessageBox.Show(message, "Error");
                    break;
                case LoggerEvent.Warning:
                    if (ShowErrorMessage)
                    {
                        MessageBox.Show(message, "Warning");
                    }
                    else
                    {
                        action("An error has occurred. Turn on \"Show all error messages\" to see details next time.");
                    }
                    break;
                default:
                    action(message);
                    break;
            }
        }
    }
}
