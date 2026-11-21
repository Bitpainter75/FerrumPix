Imports System.Collections.Generic
Imports FerrumPix.Services

Namespace ViewModels

    ''' <summary>Eine Zeile der Seite Technik: eine Bibliothek und wie es um ihren Rueckfall auf die
    ''' Fassung des Systems steht. Der Text ist schon uebersetzt.</summary>
    Public NotInheritable Class SystemLibraryFallbackRow
        Public Sub New(name As String, status As String)
            Me.Name = name
            Me.Status = status
        End Sub

        Public ReadOnly Property Name As String
        Public ReadOnly Property Status As String
    End Class

    ''' <summary>Der Rueckfall auf die Bibliotheken des Systems, wie ihn die Seite Technik zeigt
    ''' (siehe <see cref="SystemLibraryFallback"/>).</summary>
    Partial Public Class SettingsViewModel

        ''' <summary>Die Bibliotheken mit Rueckfall, in der Reihenfolge der Anzeige. Eine weitere
        ''' kommt mit einer Zeile hier dazu; Darstellung und Texte sind fuer alle dieselben.</summary>
        Private Shared Function SystemLibraryFallbacks() As IEnumerable(Of SystemLibraryFallback)
            Return {RawDecodeService.SystemFallback, HeifDecodeService.SystemFallback, JxlDecodeService.SystemFallback}
        End Function

        ''' <summary>Je Bibliothek eine Zeile. Wird bei jedem Abruf neu gebaut: der Stand aendert
        ''' sich, sobald ein Rueckfall geladen wird, und nach einem Sprachwechsel meldet
        ''' <see cref="RefreshLocalization"/> die Eigenschaft neu.</summary>
        Public ReadOnly Property SystemLibraryFallbackRows As IReadOnlyList(Of SystemLibraryFallbackRow)
            Get
                Dim rows As New List(Of SystemLibraryFallbackRow)()
                For Each fallback In SystemLibraryFallbacks()
                    Dim status As String
                    Try
                        status = DescribeSystemFallback(fallback)
                    Catch ex As Exception
                        DiagnosticLogService.LogException("Settings.SystemLibraryFallback", ex)
                        status = LocalizationService.T("Keine Fassung des Systems gefunden")
                    End Try
                    rows.Add(New SystemLibraryFallbackRow(fallback.LibraryName, status))
                Next
                Return rows
            End Get
        End Property

        ''' <summary>Die Datei, in der jeder Rueckfall steht, auch ohne Diagnoseprotokoll.</summary>
        Public ReadOnly Property SystemFallbackLogFile As String
            Get
                Return DiagnosticLogService.ErrorLogFilePath
            End Get
        End Property

        Private Shared Function DescribeSystemFallback(fallback As SystemLibraryFallback) As String
            Select Case fallback.State
                Case SystemFallbackState.NotApplicable
                    If SystemLibraryFallback.IsFlatpak Then
                        Return LocalizationService.T("Im Flatpak nicht vorgesehen, dort gibt es keine Bibliotheken des Systems")
                    End If
                    Return LocalizationService.T("Auf diesem Betriebssystem nicht vorgesehen")
                Case SystemFallbackState.PrimaryNotBundled
                    Return LocalizationService.T("Nicht nötig, es läuft keine mitgelieferte Fassung")
                Case SystemFallbackState.NotFound
                    Return LocalizationService.T("Keine Fassung des Systems gefunden")
                Case SystemFallbackState.Ready
                    Return String.Format(LocalizationService.T("Bereit: Fassung {0} unter {1}, sie wird erst geladen, wenn die mitgelieferte an einer Datei scheitert"),
                                         fallback.Version, SystemLibraryFallback.ResolveFinalPath(fallback.CandidatePath))
                Case SystemFallbackState.Loaded
                    Return String.Format(LocalizationService.T("Geladen: Fassung {0} unter {1}, bisher {2} Dateien damit gelesen"),
                                         fallback.Version, fallback.LoadedPath, fallback.SystemReads)
                Case Else
                    Return String.Format(LocalizationService.T("Gefunden unter {0}, aber nicht nutzbar, der Grund steht im Protokoll"),
                                         SystemLibraryFallback.ResolveFinalPath(fallback.CandidatePath))
            End Select
        End Function

    End Class

End Namespace
