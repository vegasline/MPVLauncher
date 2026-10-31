// Original implementation, MIT licensed.
//
// Part of the main window: the Report page.
//
// A report is a kind, a message and up to a handful of attachments, posted to
// the project's Discord webhook. There is no queue and no retry: the status
// line says what happened and the user can press Send again.

using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using Microsoft.Win32;
using MpvLauncher.Gui.Services;

namespace MpvLauncher.Gui
{
    public partial class MainWindow
    {
        #region Report

        private readonly ObservableCollection<ReportAttachment> _reportFiles = new();

        private bool _reportBusy;

        /// <summary>Hands the attachment list to the template; called once.</summary>
        private void InitializeReportPage()
        {
            LstReportFiles.ItemsSource = _reportFiles;
            PopulateReportKinds();
            UpdateReportNotes();
        }

        /// <summary>
        /// Names the controls whose visible text is drawn inside them.
        ///
        /// A button whose content is a stack of a path and a label has no name
        /// of its own, so a screen reader announces nothing at all. The names
        /// are set from the language packs with the labels, not in XAML, so
        /// they follow a language change like everything else.
        /// </summary>
        private void UpdateReportAutomationNames()
        {
            System.Windows.Automation.AutomationProperties.SetName(
                BtnReportAddFile, _locService.Get("report_add_file", "Add file"));
            System.Windows.Automation.AutomationProperties.SetName(
                LstReportKind, _locService.Get("report_kind_label", "Type"));
            System.Windows.Automation.AutomationProperties.SetName(
                TxtReportMessage, _locService.Get("report_message_label", "Message"));
        }

        /// <summary>
        /// Rebuilds the kind list, keeping the selection.
        ///
        /// The items are boxes rather than strings because each one carries the
        /// id the payload needs, and a language change must relabel them
        /// without losing which one the user picked.
        /// </summary>
        private void PopulateReportKinds()
        {
            int selected = LstReportKind.SelectedIndex;

            LstReportKind.Items.Clear();
            AddReportKind("suggestion", "report_kind_suggestion", "Suggestion");
            AddReportKind("feature", "report_kind_feature", "Feature request");
            AddReportKind("bug", "report_kind_bug", "Bug");

            LstReportKind.SelectedIndex = selected < 0 ? 0 : selected;
        }

        private void AddReportKind(string id, string key, string fallback)
        {
            LstReportKind.Items.Add(new ListBoxItem
            {
                Content = _locService.Get(key, fallback),
                Tag = id,
                Padding = new Thickness(8, 5, 8, 5),
            });
        }

        /// <summary>
        /// Shows the VPN note only in Turkish.
        ///
        /// Discord is blocked in Turkey, and a send that fails with no
        /// explanation reads as a broken button rather than a blocked network.
        /// </summary>
        private void UpdateReportNotes()
        {
            bool turkish = string.Equals(_locService.CurrentLanguage, "tr", StringComparison.OrdinalIgnoreCase);
            TxtReportTrNote.Visibility = turkish ? Visibility.Visible : Visibility.Collapsed;
        }

        private string SelectedReportKind =>
            LstReportKind.SelectedItem is ListBoxItem { Tag: string id } ? id : "suggestion";

        private string SelectedReportKindLabel =>
            LstReportKind.SelectedItem is ListBoxItem item ? item.Content?.ToString() ?? "" : "";

        private void BtnReportAddFile_Click(object sender, RoutedEventArgs e)
        {
            if (_reportFiles.Count >= ReportService.MaxFiles)
            {
                SetReportStatus(
                    _locService.Format("report_file_limit", "You can attach at most {0} files.", ReportService.MaxFiles),
                    error: true);
                return;
            }

            var dialog = new OpenFileDialog
            {
                Multiselect = true,
                Title = _locService.Get("report_add_file", "Add file"),
                Filter = _locService.Get("report_file_filter", "All files|*.*"),
            };

            if (dialog.ShowDialog(this) != true) return;

            bool rejected = false;

            foreach (string path in dialog.FileNames)
            {
                if (_reportFiles.Count >= ReportService.MaxFiles)
                {
                    SetReportStatus(
                        _locService.Format("report_file_limit", "You can attach at most {0} files.", ReportService.MaxFiles),
                        error: true);
                    rejected = true;
                    break;
                }

                var info = new FileInfo(path);
                if (!info.Exists) continue;

                if (info.Length > ReportService.MaxFileBytes)
                {
                    long limitMb = ReportService.MaxFileBytes / (1024 * 1024);
                    SetReportStatus(
                        _locService.Format("report_file_too_large", "{0} is larger than {1} MB.", info.Name, limitMb),
                        error: true);
                    rejected = true;
                    continue;
                }

                if (_reportFiles.Any(f => string.Equals(f.Path, path, StringComparison.OrdinalIgnoreCase)))
                    continue;

                _reportFiles.Add(new ReportAttachment { Path = path, Bytes = info.Length });
            }

            if (!rejected) SetReportStatus("", error: false);
        }

        private void BtnReportRemoveFile_Click(object sender, RoutedEventArgs e)
        {
            if (sender is Button { Tag: ReportAttachment attachment })
                _reportFiles.Remove(attachment);
        }

        private async void BtnReportSend_Click(object sender, RoutedEventArgs e)
        {
            if (_reportBusy) return;

            string message = TxtReportMessage.Text.Trim();
            if (message.Length < 5)
            {
                SetReportStatus(_locService.Get("report_need_message", "Please write a message first."), error: true);
                TxtReportMessage.Focus();
                return;
            }

            _reportBusy = true;
            BtnReportSend.IsEnabled = false;
            BtnReportAddFile.IsEnabled = false;
            SetReportStatus(_locService.Get("report_sending", "Sending..."), error: false);

            try
            {
                var request = new ReportRequest
                {
                    Kind = SelectedReportKind,
                    KindLabel = SelectedReportKindLabel,
                    Message = message,
                    AppVersion = UpdateService.CurrentVersion,
                    OsVersion = Environment.OSVersion.VersionString,
                    Language = _locService.CurrentLanguage,
                    Files = _reportFiles.ToList(),
                };

                var result = await ReportService.SendAsync(request);

                if (result.Ok)
                {
                    TxtReportMessage.Clear();
                    _reportFiles.Clear();
                    SetReportStatus(_locService.Get("report_sent", "Report sent. Thank you!"), error: false);
                }
                else
                {
                    SetReportStatus(
                        _locService.Format("report_failed", "Could not send: {0}", result.Error ?? ""),
                        error: true);
                }
            }
            finally
            {
                _reportBusy = false;
                BtnReportSend.IsEnabled = true;
                BtnReportAddFile.IsEnabled = true;
            }
        }

        private void SetReportStatus(string text, bool error)
        {
            TxtReportStatus.Text = text;
            TxtReportStatus.Visibility = string.IsNullOrEmpty(text) ? Visibility.Collapsed : Visibility.Visible;
            TxtReportStatus.Foreground = error ? UiTheme.Current.Danger : UiTheme.Current.Success;
        }

        #endregion
    }
}
