using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using Dsh.Contract.Methods;

namespace Dsh.Wpf;

/// <summary>One option with its selection state (bindable).</summary>
public partial class OptionUi : ObservableObject
{
    public string Label { get; }

    [ObservableProperty]
    private bool isSelected;

    public OptionUi(string label) => Label = label;
}

/// <summary>
/// Bindable question for the question panel: renders the prompt and options, and collects
/// the user's selection (and optional custom text).
/// </summary>
public partial class QuestionUi : ObservableObject
{
    public string Id { get; }

    public string Question { get; }

    public string? Header { get; }

    public string? Detail { get; }

    public bool MultiSelect { get; }

    /// <summary>Original presentation intent kind (e.g. <c>plan-review</c>), exposed for kind-specific rendering (D4).</summary>
    public string Kind => Intent?.Kind ?? "";

    /// <summary>True when the question is a plan-review approval (intent kind <c>plan-review</c>).</summary>
    public bool IsPlanReview => Kind == "plan-review";

    /// <summary>Approve text for a plan-review item; null for ordinary questions.</summary>
    public string? Approve => Intent?.Approve;

    /// <summary>The raw intent payload (may be null for ordinary questions).</summary>
    public QuestionIntent? Intent { get; }

    public ObservableCollection<OptionUi> Options { get; } = new();

    /// <summary>Selected option labels (computed from <see cref="Options"/>).</summary>
    public IEnumerable<string> SelectedLabels => Options.Where(o => o.IsSelected).Select(o => o.Label);

    [ObservableProperty]
    private string custom = "";

    public QuestionUi(QuestionItem item)
    {
        Id = item.Id;
        Question = item.Question;
        Header = item.Header;
        Detail = item.Detail;
        MultiSelect = item.MultiSelect;
        Intent = item.Intent;

        if (item.Options is not null)
        {
            foreach (var opt in item.Options)
            {
                Options.Add(new OptionUi(opt.Label));
            }
        }
    }
}
