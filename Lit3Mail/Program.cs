namespace Lit3Mail
{
    internal static class Program
    {
        /// <summary>
        ///  The main entry point for the application.
        /// </summary>
        [STAThread]
        static void Main(string[] args)
        {
            // To customize application configuration such as set high DPI settings or default font,
            // see https://aka.ms/applicationconfiguration.
            ApplicationConfiguration.Initialize();
            MailForm form;
            try
            {
                LocalDataReset.ApplyPending();
                form = new MailForm();
            }
            catch (Exception error) when (error is Microsoft.Data.Sqlite.SqliteException or
                IOException or UnauthorizedAccessException or InvalidOperationException or FormatException or
                System.Text.Json.JsonException or System.ComponentModel.Win32Exception)
            {
                ThemedPopup.Show($"Could not prepare local data or open the account database. {error.Message}",
                    "Lit3Mail", MessageBoxButtons.OK, MessageBoxIcon.Error);
                return;
            }

            if (args.Length > 0)
                form.Shown += async (_, _) => await form.OpenEmlAsync(args[0]);
            Application.Run(form);
        }
    }
}

