Imports System
Imports System.Collections.Generic
Imports System.Collections.ObjectModel
Imports System.Globalization
Imports System.Linq
Imports System.Windows.Input
Imports Avalonia.Threading
Imports ReactiveUI
Imports FerrumPix.Services

Namespace ViewModels

    ''' <summary>Eine Freigabe in der Liste der Einstellungen: Titel, eine Zeile mit Besitzer, Inhalt,
    ''' Ablauf, Aufrufen und Zustand, dazu Widerrufen.</summary>
    Public NotInheritable Class ShareAdminRow
        ''' <param name="isInert">Gilt die Freigabe gerade nicht, obwohl sie nicht abgelaufen ist
        ''' (Besitzer gesperrt, Ordner weg, kein Recht mehr)? Im Hintergrund ermittelt, weil die
        ''' Frage bei einem Ordner das Laufwerk anfasst.</param>
        Public Sub New(record As ShareRecord, revoke As Action(Of ShareAdminRow), isInert As Boolean)
            Me.Record = record
            Me.IsInert = isInert
            RevokeCommand = New DelegateCommand(Sub() revoke(Me))
        End Sub

        Public ReadOnly Property Record As ShareRecord
        Public ReadOnly Property IsInert As Boolean
        Public ReadOnly Property RevokeCommand As ICommand

        Public ReadOnly Property Title As String
            Get
                Return If(String.IsNullOrWhiteSpace(Record.Title), "FerrumPix", Record.Title)
            End Get
        End Property

        ''' <summary>Je Teil ein ganzer Text fuer die Uebersetzung, beim Lesen uebersetzt.</summary>
        Public ReadOnly Property Details As String
            Get
                Dim parts As New List(Of String)()
                Dim owner = UserAccountService.Find(Record.OwnerUserId)
                Dim ownerName = If(owner Is Nothing, LocalizationService.T("Gelöschter Benutzer"),
                                   If(owner.Name.Length > 0, owner.Name, LocalizationService.T("Eigentümer")))
                parts.Add(String.Format(CultureInfo.CurrentCulture, LocalizationService.T("Geteilt von {0}"), ownerName))
                If Record.KindValue() = ShareKind.Folder Then
                    parts.Add(String.Format(CultureInfo.CurrentCulture, LocalizationService.T("Ordner {0}"), BrowserBridge.FolderDisplayName(Record.Folder)))
                    If Record.IncludeSubfolders Then parts.Add(LocalizationService.T("Mit Unterordnern"))
                Else
                    parts.Add(String.Format(CultureInfo.CurrentCulture, LocalizationService.T("{0} Bilder"), Record.Paths.Count))
                End If
                Dim now = AppLockService.Clock()
                Dim expiry = ShareStore.ExpiryOf(Record)
                If ShareStore.IsExpired(Record, now) Then
                    parts.Add(LocalizationService.T("Abgelaufen"))
                ElseIf expiry.HasValue Then
                    parts.Add(String.Format(CultureInfo.CurrentCulture, LocalizationService.T("Verfügbar bis {0}"),
                                            expiry.Value.ToLocalTime().ToString("d", CultureInfo.CurrentCulture)))
                Else
                    parts.Add(LocalizationService.T("Unbegrenzt"))
                End If
                parts.Add(String.Format(CultureInfo.CurrentCulture, LocalizationService.T("{0} Aufrufe"), Record.Views))
                If Not ShareStore.IsExpired(Record, now) AndAlso IsInert Then
                    parts.Add(LocalizationService.T("Ohne Wirkung"))
                End If
                Return String.Join(", ", parts)
            End Get
        End Property
    End Class

    ''' <summary>
    ''' Der Abschnitt "Freigaben" der Einstellungen, gleich unter den Benutzern: alle Freigaben per
    ''' Link aller Benutzer (ShareStore), Widerrufen je Freigabe und "Alle Freigaben pausieren".
    ''' Die Einstellungen sieht nur ein Administrator; jede Aenderung wirkt sofort, wie bei den
    ''' Benutzern. Anlegen und Bearbeiten geschehen in der mobilen Seite bzw. ueber das
    ''' Kontextmenue der Galerie im Browser.
    ''' </summary>
    Partial Public Class SettingsViewModel

        Private ReadOnly _shareRows As New ObservableCollection(Of ShareAdminRow)()
        Private _sharesLoaded As Boolean

        Public ReadOnly Property ShareRows As ObservableCollection(Of ShareAdminRow)
            Get
                EnsureSharesLoaded()
                Return _shareRows
            End Get
        End Property

        Public ReadOnly Property HasNoShares As Boolean
            Get
                EnsureSharesLoaded()
                ' Erst nach dem ersten Laden: vorher stuende kurz "Noch keine Freigaben" da.
                Return _shareRowsReady AndAlso _shareRows.Count = 0
            End Get
        End Property

        ''' <summary>Alle Freigaben angehalten: jeder Link antwortet wie ein unbekannter, bis der
        ''' Schalter wieder aus ist.</summary>
        Public Property SharesPaused As Boolean
            Get
                Return ShareStore.Paused
            End Get
            Set(value As Boolean)
                If value = ShareStore.Paused Then Return
                ShareStore.SetPaused(value)
                Dim actor = SecurityLog.DesktopActor()
                SecurityLog.SharesPaused(value, actor.User, actor.From)
                Me.RaisePropertyChanged(NameOf(SharesPaused))
                ReloadShareRows()
            End Set
        End Property

        ''' <summary>Liest die Freigaben beim ersten Zugriff des Abschnitts und folgt danach jeder
        ''' Aenderung (angelegt in der mobilen Seite, widerrufen, Aufrufe).</summary>
        Private Sub EnsureSharesLoaded()
            If _sharesLoaded Then Return
            _sharesLoaded = True
            AddHandler ShareStore.Changed, Sub(s, e) Dispatcher.UIThread.Post(AddressOf ReloadShareRows)
            ReloadShareRows()
        End Sub

        ''' <summary>Zaehlt jedes Neuladen; ein ueberholtes verwirft sein Ergebnis.</summary>
        Private _shareRowsGeneration As Integer
        Private _shareRowsReady As Boolean

        ''' <summary>So lange wartet die Liste auf die Frage, welche Freigaben gerade wirken. Was bis
        ''' dahin keine Antwort hat (ein haengendes Netzlaufwerk), steht ohne den Zusatz da.</summary>
        Private Const ShareStateWaitMs As Integer = 3000

        ''' <summary>Liest die Freigaben neu. Ob eine wirkt, fragt bei einem Ordner das Laufwerk
        ''' (Directory.Exists, Rechte); das geschieht im Hintergrund und mit Zeitgrenze, ein Ordner
        ''' auf einem nicht erreichbaren Laufwerk (OfflineVolumes) wird gar nicht erst angefasst.
        ''' Frueher fragte die Zeile beim Zeichnen auf dem Anzeigefaden, und ein abgehaengtes SMB-
        ''' oder NFS-Laufwerk liess die Einstellungen haengen.</summary>
        Private Async Sub ReloadShareRows()
            Dim generation = Threading.Interlocked.Increment(_shareRowsGeneration)
            Dim inert As New Concurrent.ConcurrentDictionary(Of String, Boolean)(StringComparer.Ordinal)
            Dim records As List(Of ShareRecord) = Nothing
            Dim work = Threading.Tasks.Task.Run(
                Sub()
                    records = ShareStore.All()
                    For Each record In records
                        If record.KindValue() = ShareKind.Folder AndAlso OfflineVolumes.IsFolderUnreachable(record.Folder) Then
                            inert(record.Id) = True
                            Continue For
                        End If
                        inert(record.Id) = Not BrowserBridge.IsShareActive(record)
                    Next
                End Sub)
            Dim finished = Await Threading.Tasks.Task.WhenAny(work, Threading.Tasks.Task.Delay(ShareStateWaitMs))
            If generation <> _shareRowsGeneration Then Return
            If records Is Nothing Then
                ' Nicht einmal die Ablage kam in der Zeit: dann eben warten, die Zeilen brauchen sie.
                Try
                    Await work
                Catch ex As Exception
                    DiagnosticLogService.LogException("Settings.Shares", ex)
                End Try
                If generation <> _shareRowsGeneration OrElse records Is Nothing Then Return
            End If
            _shareRows.Clear()
            For Each record In records
                Dim isInert = False
                inert.TryGetValue(record.Id, isInert)
                _shareRows.Add(New ShareAdminRow(record, AddressOf RevokeShareRow, isInert))
            Next
            _shareRowsReady = True
            Me.RaisePropertyChanged(NameOf(HasNoShares))
            Me.RaisePropertyChanged(NameOf(SharesPaused))
            If finished IsNot work Then
                ' Die langsamen Antworten kommen nach: einmal neu zeichnen, wenn alle da sind.
                Try
                    Await work
                Catch ex As Exception
                    DiagnosticLogService.LogException("Settings.Shares", ex)
                    Return
                End Try
                If generation <> _shareRowsGeneration Then Return
                For i = 0 To _shareRows.Count - 1
                    Dim row = _shareRows(i)
                    Dim isInert = False
                    inert.TryGetValue(row.Record.Id, isInert)
                    If isInert <> row.IsInert Then _shareRows(i) = New ShareAdminRow(row.Record, AddressOf RevokeShareRow, isInert)
                Next
            End If
        End Sub

        Private Async Sub RevokeShareRow(row As ShareAdminRow)
            If row Is Nothing Then Return
            If _mainVm IsNot Nothing Then
                Dim confirmed = Await _mainVm.ShowConfirmAsync(row.Title,
                    LocalizationService.T("Diese Freigabe widerrufen? Der Link funktioniert danach nicht mehr."), "Widerrufen", "Abbrechen")
                If Not confirmed Then Return
            End If
            ShareStore.Revoke(row.Record.Id)
            Dim actor = SecurityLog.DesktopActor()
            SecurityLog.ShareEvent("share.revoked", actor.User, actor.From, row.Record.Id)
            ReloadShareRows()
        End Sub

    End Class

End Namespace
