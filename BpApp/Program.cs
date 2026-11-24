namespace BpApp;

static class Program
{
    [STAThread]
    static void Main()
    {
        // Весь интерфейс на русском: даты на графике и в таблицах в формате дд.мм.гггг
        var ru = new System.Globalization.CultureInfo("ru-RU");
        System.Globalization.CultureInfo.DefaultThreadCurrentCulture = ru;
        System.Globalization.CultureInfo.DefaultThreadCurrentUICulture = ru;
        System.Globalization.CultureInfo.CurrentCulture = ru;
        System.Globalization.CultureInfo.CurrentUICulture = ru;
        Application.SetHighDpiMode(HighDpiMode.SystemAware);
        Application.EnableVisualStyles();
        Application.SetCompatibleTextRenderingDefault(false);
        Application.ThreadException += (_, e) =>
            MessageBox.Show(e.Exception.ToString(), "Непредвиденная ошибка", MessageBoxButtons.OK, MessageBoxIcon.Error);
        Application.Run(new MainForm());
    }
}
