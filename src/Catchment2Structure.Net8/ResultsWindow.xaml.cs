using System.Windows;

namespace Catchment2Structure
{
    public partial class ResultsWindow : Window
    {
        public ResultsWindow(string summary)
        {
            InitializeComponent();
            SummaryBox.Text = summary ?? "";
        }

        private void Close_Click(object sender, RoutedEventArgs e)
        {
            Close();
        }
    }
}
