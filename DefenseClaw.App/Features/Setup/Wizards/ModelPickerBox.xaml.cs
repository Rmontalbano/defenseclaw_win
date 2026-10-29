using System.Windows.Controls;
using System.Windows.Input;
using DefenseClaw.App.Services.Wizards;
using DefenseClaw.App.ViewModels.Wizards;

namespace DefenseClaw.App.Views.Wizards;

/// <summary>
/// The searchable model box of a model field (see <c>ModelPickerBox.xaml</c>): all of its state is the field's (<see cref="WizardFieldViewModel"/>:
/// the rows, the highlighted row, whether the list is open); this only turns keys and clicks into those calls, in the text box where the focus
/// stays.
/// </summary>
public partial class ModelPickerBox : UserControl
{
    public ModelPickerBox()
    {
        InitializeComponent();
    }

    private WizardFieldViewModel? Field => DataContext as WizardFieldViewModel;

    private void OnInputGotFocus(object sender, KeyboardFocusChangedEventArgs e) => Field?.OpenModelList();

    private void OnInputLostFocus(object sender, KeyboardFocusChangedEventArgs e)
    {
        // Focus that stays inside the box (it never goes to a row) keeps the list; focus that leaves closes it.
        if (!IsKeyboardFocusWithin)
        {
            Field?.CloseModelList();
        }
    }

    private void OnInputTextChanged(object sender, TextChangedEventArgs e)
    {
        // Typing shows the suggestions again after they were closed. A change made for the operator (a model chosen, a goal's preset) while the
        // box has focus ends with the list closed by whoever made it.
        if (Input.IsKeyboardFocusWithin && e.Changes.Count > 0)
        {
            Field?.OpenModelList();
        }
    }

    private void OnInputKeyDown(object sender, KeyEventArgs e)
    {
        if (Field is not { } field)
        {
            return;
        }

        switch (e.Key)
        {
            case Key.Down:
                field.MoveModelSelection(1);
                e.Handled = true;
                break;

            case Key.Up:
                field.MoveModelSelection(-1);
                e.Handled = true;
                break;

            case Key.Enter when field.AcceptModelSelection():
                e.Handled = true;
                break;

            // The first Esc closes the list; the wizard sees the second.
            case Key.Escape when field.IsModelListOpen:
                field.CloseModelList();
                e.Handled = true;
                break;
        }
    }

    private void OnRowMouseUp(object sender, MouseButtonEventArgs e)
    {
        if (sender is ListBoxItem { DataContext: ModelPickerRow row } && Field is { } field)
        {
            // The rows never take focus, so it is still in the box.
            field.ChooseModelCommand.Execute(row);
            e.Handled = true;
        }
    }
}
