using System.Windows;
using System.Windows.Input;

namespace KeyMacro
{
    public partial class InputDialog : Window
    {
        public string ResponseText => InputTextBox.Text;

        public InputDialog(string prompt, string defaultText = "")
        {
            InitializeComponent();
            PromptTextBlock.Text = prompt;
            InputTextBox.Text = defaultText;
            InputTextBox.Focus();
            InputTextBox.SelectAll();
        }

        private void SaveButton_Click(object sender, RoutedEventArgs e)
        {
            if (string.IsNullOrWhiteSpace(InputTextBox.Text))
            {
                MessageBox.Show("프로필 이름을 올바르게 입력해야 합니다.", "입력 오류", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }
            DialogResult = true;
        }

        private void CancelButton_Click(object sender, RoutedEventArgs e)
        {
            DialogResult = false;
        }

        private void Window_MouseDown(object sender, MouseButtonEventArgs e)
        {
            if (e.ChangedButton == MouseButton.Left)
            {
                DragMove();
            }
        }
    }
}
