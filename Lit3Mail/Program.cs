namespace Lit3Mail
{
    internal static class Program
    {
        /// <summary>
        ///  The main entry point for the application.
        /// </summary>
        [STAThread]
        static void Main()
        {
            // To customize application configuration such as set high DPI settings or default font,
            // see https://aka.ms/applicationconfiguration.
            ApplicationConfiguration.Initialize();
            MailForm form;
            try
            {
                form = new MailForm();
            }
            catch (Exception error) when (error is Microsoft.Data.Sqlite.SqliteException or
                IOException or UnauthorizedAccessException or InvalidOperationException or FormatException)
            {
                ThemedPopup.Show($"Could not open the local account database. {error.Message}",
                    "Lit3Mail", MessageBoxButtons.OK, MessageBoxIcon.Error);
                return;
            }

            Application.Run(form);
        }
    }
}

