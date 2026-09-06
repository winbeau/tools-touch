using System.Globalization;
using System.IO;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using ToolsTouch.Application;
using ToolsTouch.Core;
using ToolsTouch.Desktop;
using ToolsTouch.Infrastructure.Tracking;

internal static partial class Program
{
    private static TaskCompletionSource? mailResponseGate;
    private static void RunWorkspaceUiChecks(Action<string, Action> test, MainViewModel model, MainWindow window,
        TabControl tabs, LocalDatabase database, string draftId, string output)
    {
        test("grouped navigation, contextual return and unsaved draft survive page switches", () =>
        {
            var navigation = (ListBox)window.FindName("WorkspaceNavigation");
            Check(navigation.Items.Count == 10 && model.Navigation.Select(item => item.Group).Distinct().Count() == 7,
                "grouped navigation lost an entry");
            foreach (var entry in model.Navigation)
            {
                navigation.SelectedValue = entry.Id; Pump();
                Check(model.SelectedPage == entry.Id && tabs.SelectedIndex == entry.Id && model.PageTitle == entry.Title,
                    "sidebar selected a different page: " + entry.Title);
            }
            model.SelectedPage = (int)WorkspacePage.Discover;
            model.SelectedProfessor = model.Professors.Single();
            model.ViewProfessorCommand.Execute(null); Pump();
            Check(model.SelectedPage == (int)WorkspacePage.ProfessorDetail && model.SelectedNavigationPage == (int)WorkspacePage.Discover,
                "context navigation lost its origin");
            model.BackToProfessorsCommand.Execute(null); Pump();
            Check(model.SelectedPage == (int)WorkspacePage.Discover && model.SelectedProfessor != null, "back lost list selection");
            model.SelectedDraft = model.Drafts.Single(item => item.Id == draftId);
            model.SelectedPage = (int)WorkspacePage.Outreach; Pump();
            var body = Editor(window, "Body"); body.Text = "切换页面仍保留的未保存正文"; Pump();
            model.SelectedPage = (int)WorkspacePage.Settings; Pump();
            model.SelectedPage = (int)WorkspacePage.Outreach; Pump();
            Check(ReferenceEquals(body, Editor(window, "Body")) && body.Text == "切换页面仍保留的未保存正文", "navigation recreated or cleared the draft editor");
            model.SelectedPage = -1;
            Check(model.SelectedPage == (int)WorkspacePage.Outreach, "invalid transient selection replaced the active page");
        });

        test("record filters, selection and pending edits survive refresh with optimistic revisions", () =>
        {
            model.SelectedPage = (int)WorkspacePage.Records; Pump();
            var records = model.RecordGrid;
            records.NewCollectionName = "界面验证 · 申请进度";
            records.CreateCollectionCommand.Execute(null); Pump();
            records.NewFieldKey = "note"; records.NewFieldName = "研究与申请备注"; records.NewFieldType = "Text";
            records.CreateFieldCommand.Execute(null); Pump();
            for (var i = 0; i < 4; i++) { records.CreateRecordCommand.Execute(null); Pump(); }
            Check(records.Rows.Count == 4 && records.Fields.Count == 1, "record fixture commands failed");
            records.SelectedRow = records.Rows[2]; records.EditValue = "尚未保存的记录备注";
            var rowId = records.SelectedRow.RecordId; var fieldId = records.SelectedField!.Id;
            records.SelectedFilterField = records.SelectedField; records.FilterOperator = "contains"; records.FilterValue = "";
            records.SelectedSortField = records.SelectedField; records.SortDirection = "desc";
            records.RefreshCommand.Execute(null); Pump();
            Check(records.SelectedRow?.RecordId == rowId && records.SelectedField?.Id == fieldId && records.EditValue == "尚未保存的记录备注",
                "record refresh discarded selection or pending edit");
            Check(records.SelectedFilterField?.Id == fieldId && records.SelectedSortField?.Id == fieldId && records.SortDirection == "desc",
                "record refresh cleared query controls");
            records.SaveCellCommand.Execute(null); Pump();
            Check(records.Rows.Any(row => row.DisplayValue.Contains("尚未保存的记录备注")), "record editor did not save to isolated SQLite");
            records.FilterValue = "definitely-no-matches"; records.ApplyFilterCommand.Execute(null); Pump();
            Check(records.Rows.Count == 0, "filter was cleared before query execution");
            Snapshot(window, Path.Combine(output, "records-no-results.png"));
            records.ClearFilterCommand.Execute(null); Pump();
            records.NewViewName = "已保存视图"; records.SaveViewCommand.Execute(null); Pump();
            var viewId = records.SelectedView?.Id;
            records.EditValue = "视图刷新仍保留";
            records.RefreshCommand.Execute(null); Pump();
            Check(viewId != null && records.SelectedView?.Id == viewId && records.EditValue == "视图刷新仍保留", "refresh lost saved view or editor");
            Snapshot(window, Path.Combine(output, "records-populated.png"));
        });

        test("application details retain pending edits and material selection during refresh", () =>
        {
            Execute(database, "INSERT INTO School(Id,CanonicalName,ShortName,CreatedAt,UpdatedAt) VALUES('ui-school','界面测试大学','测试大学','2026-09-05','2026-09-05')");
            Execute(database, "INSERT INTO Department(Id,SchoolId,CanonicalName,Kind,CreatedAt,UpdatedAt) VALUES('ui-dept','ui-school','计算机学院','School','2026-09-05','2026-09-05')");
            var service = new ApplicationCaseService(database);
            var record = service.Create(new ApplicationCaseCreateRequest("ui-dept", 2026, "Master", Stage: "Preparing", NextStep: "整理申请材料", Id: "ui-case"));
            service.AddMaterial(new ApplicationMaterialCreateRequest(record.Id, "个人简历", true, "Ready", null, "合成资料", "ui-material"));
            model.ApplicationWorkspace.Refresh();
            model.SelectedPage = (int)WorkspacePage.Applications; Pump();
            var application = model.ApplicationWorkspace;
            var materialId = application.SelectedMaterial?.Id;
            application.OwnerNote = "尚未保存的申请备注"; application.NextStep = "核对推荐信";
            application.MaterialNote = "材料备注尚未保存";
            model.RefreshCommand.Execute(null); Pump();
            Check(application.SelectedCase?.Case.Id == record.Id && application.OwnerNote == "尚未保存的申请备注" &&
                application.NextStep == "核对推荐信" && application.SelectedMaterial?.Id == materialId && application.MaterialNote == "材料备注尚未保存",
                "application refresh discarded pending form state");
            model.SelectedPage = (int)WorkspacePage.Dashboard; Pump();
            model.SelectedPage = (int)WorkspacePage.Applications; Pump();
            Check(Editor(window, "ApplicationWorkspace.OwnerNote").Text == "尚未保存的申请备注", "application navigation lost form state");
            application.SaveDetailsCommand.Execute(null); Pump();
            Check(service.Get(record.Id).OwnerNote == "尚未保存的申请备注", "application editor did not persist");
            Snapshot(window, Path.Combine(output, "applications-populated.png"));
        });

        test("native keyboard focus, combo labels and validation feedback", () =>
        {
            model.SelectedPage = (int)WorkspacePage.Outreach; Pump();
            var recipient = Editor(window, "Recipient");
            window.Activate(); recipient.Focus();
            Check(recipient.MoveFocus(new TraversalRequest(FocusNavigationDirection.Next)), "Tab focus could not leave recipient");
            Check(Keyboard.FocusedElement is TextBox next && BindingOperations.GetBinding(next, TextBox.TextProperty)?.Path.Path == "Subject", "Tab order skips the subject");
            ((TextBox)Keyboard.FocusedElement).MoveFocus(new TraversalRequest(FocusNavigationDirection.Next));
            Check(ReferenceEquals(Keyboard.FocusedElement, Editor(window, "Body")), "Tab order skips the body");
            model.SelectedPage = (int)WorkspacePage.Settings; Pump();
            var provider = Descendants<ComboBox>(window).Single(control => BindingOperations.GetBinding(control, ItemsControl.ItemsSourceProperty)?.Path.Path == "Providers");
            Check(provider.ActualHeight <= 40 && !Descendants<TextBlock>(provider).Any(text => text.Text.Contains("ModelProvider {")), "combo selection rendered an object dump");
            provider.IsDropDownOpen = true; Pump();
            Check(provider.IsDropDownOpen, "native combo popup did not open"); provider.IsDropDownOpen = false;
            model.SelectedPage = (int)WorkspacePage.Admissions; Pump();
            var year = Editor(window, "AdmissionWorkspace.TargetCycleYear");
            year.Text = "不是年份"; Pump();
            Check(Validation.GetHasError(year), "invalid numeric input has no validation state");
            Snapshot(window, Path.Combine(output, "validation-error.png"));
            year.Text = "2026"; Pump(); Check(!Validation.GetHasError(year), "validation state did not clear after correction");
        });

        test("mail loading state disables edits and preserves the pending body", () =>
        {
            Execute(database, "UPDATE Outreach SET SenderAccount='sender@example.org' WHERE State='Sent'");
            model.SelectedPage = (int)WorkspacePage.Outreach; Pump();
            var body = Editor(window, "Body"); body.Text = "等待回复同步时保留的未保存正文"; Pump();
            mailResponseGate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            try
            {
                model.SyncRepliesCommand.Execute(null); Pump();
                Check(model.MailBusy && body.IsReadOnly && !model.SaveDraftCommand.CanExecute(null) && !model.SendCommand.CanExecute(null), "mail loading state did not lock pending editor operations");
                Check(Descendants<ProgressBar>(window).Any(bar => bar.IsVisible && bar.IsIndeterminate), "mail loading indicator is missing");
                Snapshot(window, Path.Combine(output, "outreach-loading.png"));
            }
            finally { mailResponseGate.TrySetResult(); mailResponseGate = null; }
            WaitFor(() => !model.MailBusy, 15); Pump();
            Check(!body.IsReadOnly && body.Text == "等待回复同步时保留的未保存正文", "reply sync discarded the pending body");
        });

        test("all pages remain bounded at minimum, standard and wide window sizes", () =>
        {
            var evidence = new List<object>();
            foreach (var size in new[] { (1040d, 720d), (1360d, 900d), (1600d, 1000d) })
            {
                window.Width = size.Item1; window.Height = size.Item2;
                for (var page = 0; page < tabs.Items.Count; page++)
                {
                    model.SelectedPage = page; Pump();
                    var view = (FrameworkElement)((TabItem)tabs.Items[page]).Content;
                    Check(view.ActualWidth > 600 && view.ActualHeight > 300, "page content squeezed below usable dimensions");
                    foreach (var button in Descendants<Button>(view).Where(button => button.IsVisible && button.Content is string))
                    {
                        var text = new FormattedText((string)button.Content, CultureInfo.GetCultureInfo("zh-CN"), FlowDirection.LeftToRight,
                            new Typeface(button.FontFamily, button.FontStyle, button.FontWeight, button.FontStretch), button.FontSize, Brushes.Black, VisualTreeHelper.GetDpi(button).PixelsPerDip);
                        Check(button.ActualWidth + 2 >= text.Width + button.Padding.Left + button.Padding.Right,
                            $"button text clipped at {size.Item1} on page {page}: {button.Content}");
                        var bounds = button.TransformToAncestor(view).TransformBounds(new Rect(button.RenderSize));
                        Check(bounds.Left >= -2 && bounds.Right <= view.ActualWidth + 2, $"button exceeds page width: {button.Content}");
                    }
                    foreach (var grid in Descendants<DataGrid>(view))
                    {
                        Check(double.IsFinite(grid.ActualHeight) && grid.ActualHeight <= view.ActualHeight && grid.EnableRowVirtualization && grid.EnableColumnVirtualization,
                            "table lost bounded height or virtualization");
                    }
                    evidence.Add(new { page, width = window.Width, height = window.Height, contentWidth = view.ActualWidth, contentHeight = view.ActualHeight });
                    Snapshot(window, Path.Combine(output, $"layout-{size.Item1:0}-page-{page + 1}.png"));
                }
            }
            File.WriteAllText(Path.Combine(output, "layout-results.json"), JsonSerializer.Serialize(evidence, new JsonSerializerOptions { WriteIndented = true }));
            window.Width = 1360; window.Height = 900; Pump();
        });

        test("native WPF handles 100, 125 and 150 percent DPI change messages", () =>
        {
            model.SelectedPage = (int)WorkspacePage.Outreach; Pump();
            var handle = new WindowInteropHelper(window).Handle;
            var initial = VisualTreeHelper.GetDpi(window);
            var evidence = new List<object>();
            var verifiedDpi = new List<int>();
            foreach (var dpi in new[] { 96, 120, 144, 96 })
            {
                GetWindowRect(handle, out var rect);
                rect.Right = rect.Left + (int)(1360d * dpi / 96);
                rect.Bottom = rect.Top + (int)(900d * dpi / 96);
                SendMessage(handle, 0x02E0, (nint)(dpi | (dpi << 16)), ref rect);
                Pump();
                var actual = VisualTreeHelper.GetDpi(window);
                if (Math.Abs(actual.PixelsPerInchX - dpi) < 0.1) verifiedDpi.Add(dpi);
                evidence.Add(new { requestedDpi = dpi, actualDpi = actual.PixelsPerInchX, actualScale = actual.DpiScaleX,
                    method = "WM_DPICHANGED sent to isolated native WPF HWND; no operating-system display setting changed" });
                for (var page = 0; page < tabs.Items.Count; page++)
                {
                    model.SelectedPage = page; Pump();
                    var view = (FrameworkElement)((TabItem)tabs.Items[page]).Content;
                    foreach (var button in Descendants<Button>(view).Where(button => button.IsVisible && button.Content is string))
                    {
                        var bounds = button.TransformToAncestor(view).TransformBounds(new Rect(button.RenderSize));
                        Check(bounds.Left >= -2 && bounds.Right <= view.ActualWidth + 2, $"button overflows at {dpi} DPI: {button.Content}");
                    }
                    Snapshot(window, Path.Combine(output, $"dpi-{dpi}-page-{page + 1}.png"));
                }
            }
            File.WriteAllText(Path.Combine(output, "dpi-results.json"), JsonSerializer.Serialize(new { initialDpi = initial.PixelsPerInchX, realMonitorTransitionsVerified = false, observations = evidence }, new JsonSerializerOptions { WriteIndented = true }));
            // A source-generated DPI event is evidence for layout handling, not a real monitor transition.
            Check(verifiedDpi.SequenceEqual(new[] { 96, 120, 144, 96 }), "WPF did not apply the requested DPI sequence");
            window.Width = 1360; window.Height = 900;
            model.SelectedPage = (int)WorkspacePage.Outreach; Pump();
        });

        test("advanced forms, settings tabs and context tabs stay reachable and preserve scroll", () =>
        {
            window.Width = 1040; window.Height = 720;
            foreach (var page in new[] { (int)WorkspacePage.Records, (int)WorkspacePage.Settings, (int)WorkspacePage.Faculty, (int)WorkspacePage.ProfessorDetail })
            {
                model.SelectedPage = page; Pump();
                var view = (FrameworkElement)((TabItem)tabs.Items[page]).Content;
                var inner = Descendants<TabControl>(view).FirstOrDefault();
                var count = inner?.Items.Count ?? 1;
                for (var index = 0; index < count; index++)
                {
                    if (inner != null) { inner.SelectedIndex = index; Pump(); }
                    foreach (var expander in Descendants<Expander>(view).ToArray()) expander.IsExpanded = true;
                    Pump();
                    foreach (var button in Descendants<Button>(view).Where(button => button.IsVisible && button.Content is string).ToArray())
                    {
                        button.BringIntoView(); Pump();
                        var bounds = button.TransformToAncestor(view).TransformBounds(new Rect(button.RenderSize));
                        Check(bounds.Left >= -2 && bounds.Right <= view.ActualWidth + 2 && bounds.Top >= -2 && bounds.Bottom <= view.ActualHeight + 2,
                            $"expanded form action cannot be reached: {button.Content}");
                    }
                    Snapshot(window, Path.Combine(output, $"advanced-{page}-{index}.png"));
                }
            }
            model.SelectedPage = (int)WorkspacePage.Settings; Pump();
            var settings = (FrameworkElement)window.FindName("SettingsView");
            Descendants<TabControl>(settings).Single().SelectedIndex = 0; Pump();
            var scroll = Descendants<ScrollViewer>(settings).First(control => control.ScrollableHeight > 20);
            scroll.ScrollToEnd(); Pump(); var offset = scroll.VerticalOffset;
            model.SelectedPage = (int)WorkspacePage.Dashboard; Pump();
            model.SelectedPage = (int)WorkspacePage.Settings; Pump();
            Check(Math.Abs(scroll.VerticalOffset - offset) < 1, "page switching reset the scroll offset");
            window.Width = 1360; window.Height = 900; Pump();
        });

        test("unknown delivery is visibly locked and cannot be resent", () =>
        {
            var outreach = new OutreachService(database);
            Execute(database, $"UPDATE Outreach SET State='Unknown' WHERE Id='{draftId}'");
            try
            {
                var unknown = outreach.Get(draftId);
                model.Drafts[model.Drafts.ToList().FindIndex(item => item.Id == draftId)] = unknown;
                model.SelectedDraft = unknown;
                model.SelectedPage = (int)WorkspacePage.Outreach; Pump();
                Check(Editor(window, "Body").IsReadOnly && !model.SendCommand.CanExecute(null) && !model.SaveDraftCommand.CanExecute(null)
                    && model.ReconcileCommand.CanExecute(null) && model.DraftEditorStatus.Contains("不会自动重试"), "Unknown delivery permits resend or lacks next-step guidance");
                Snapshot(window, Path.Combine(output, "outreach-unknown.png"));
            }
            finally
            {
                Execute(database, $"UPDATE Outreach SET State='Draft' WHERE Id='{draftId}'");
                var restored = outreach.Get(draftId);
                model.Drafts[model.Drafts.ToList().FindIndex(item => item.Id == draftId)] = restored;
                model.SelectedDraft = restored; Pump();
            }
        });
    }

    private static TextBox Editor(Window window, string path) => Descendants<TextBox>(window).Single(box => BindingOperations.GetBinding(box, TextBox.TextProperty)?.Path.Path == path);
    [StructLayout(LayoutKind.Sequential)] private struct NativeRect { public int Left, Top, Right, Bottom; }
    [DllImport("user32.dll")] private static extern bool GetWindowRect(nint handle, out NativeRect rect);
    [DllImport("user32.dll")] private static extern nint SendMessage(nint handle, uint message, nint wParam, ref NativeRect rect);
}
