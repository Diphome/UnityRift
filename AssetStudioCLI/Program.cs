using AssetStudio;
using AssetStudioCLI.Options;
using System;

namespace AssetStudioCLI
{
    class Program
    {
        public static void Main(string[] args)
        {
            var options = new CLIOptions(args);
            if (options.isParsed)
            {
                CLIRun(options);
            }
            else if (options.showHelp)
            {
                options.ShowHelp();
            }
            else
            {
                Console.WriteLine();
                options.ShowHelp(showUsageOnly: true);
            }
        }

        private static void CLIRun(CLIOptions options)
        {
            var cliLogger = new CLILogger(options);
            Logger.Default = cliLogger;
            var studio = new Studio(options);
            options.ShowCurrentOptions();

            try
            {
                if (studio.LoadAssets())
                {
                    studio.ParseAssets();
                    if (options.filterBy != FilterBy.None)
                    {
                        studio.FilterAssets();
                    }
                    if (options.o_exportAssetList.Value != ExportListType.None)
                    {
                        studio.ExportAssetList();
                    }
                    if (options.o_workMode.Value == WorkMode.Info)
                    {
                        studio.ShowExportableAssetsInfo();
                        return;
                    }
                    studio.ExportAssets();
                }
            }
            catch (Exception ex)
            {
                Logger.Error(ex.ToString());
            }
            finally
            {
                cliLogger.LogToFile(LoggerEvent.Verbose, "---Program ended---");
            }
        }       
    }
}
