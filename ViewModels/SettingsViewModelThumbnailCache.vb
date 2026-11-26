Imports System
Imports System.Collections.Generic
Imports System.Linq
Imports System.Threading.Tasks
Imports ReactiveUI
Imports FerrumPix.Services

Namespace ViewModels

    ''' <summary>Eine Stufe der Hoechstgroesse des Kachelspeichers. Der Name wird beim Lesen
    ''' uebersetzt, damit er einen Sprachwechsel mitmacht.</summary>
    Public NotInheritable Class ThumbnailCacheLimitOption
        Public Property Gigabytes As Integer

        Public ReadOnly Property Name As String
            Get
                If Gigabytes <= 0 Then Return LocalizationService.T("Unbegrenzt")
                Return $"{Gigabytes} GB"
            End Get
        End Property
    End Class

    ''' <summary>Die Hoechstgroesse des Kachelspeichers auf der Seite "Leistung" und der belegte
    ''' Platz. Der Ort der Kacheln ist der Datenordner (SettingsViewModelDataFolder.vb).</summary>
    Partial Public Class SettingsViewModel

        Private _thumbnailCacheLimitGigabytes As Integer = 0
        Private _savedThumbnailCacheLimitGigabytes As Integer = 0
        Private _thumbnailCacheUsageText As String = ""

        Public ReadOnly Property ThumbnailCacheLimitOptions As IReadOnlyList(Of ThumbnailCacheLimitOption) =
            AppSettingsService.ThumbnailCacheLimitChoices.Select(Function(g) New ThumbnailCacheLimitOption With {.Gigabytes = g}).ToList()

        ''' <summary>Die gewaehlte Stufe der Hoechstgroesse. Eine kleinere Grenze raeumt sofort im
        ''' Hintergrund auf.</summary>
        Public Property SelectedThumbnailCacheLimit As ThumbnailCacheLimitOption
            Get
                Return ThumbnailCacheLimitOptions.FirstOrDefault(Function(o) o.Gigabytes = _thumbnailCacheLimitGigabytes)
            End Get
            Set(value As ThumbnailCacheLimitOption)
                If value Is Nothing Then Return
                ApplyThumbnailCacheLimit(value.Gigabytes)
            End Set
        End Property

        Private Sub ApplyThumbnailCacheLimit(gigabytes As Integer)
            Dim normalized = AppSettingsService.NormalizeThumbnailCacheLimitGigabytes(gigabytes)
            If normalized = _thumbnailCacheLimitGigabytes Then Return
            _thumbnailCacheLimitGigabytes = normalized
            Me.RaisePropertyChanged(NameOf(SelectedThumbnailCacheLimit))
            AppSettingsService.Update(Sub(s) s.ThumbnailCacheLimitGigabytes = normalized)
            ThumbnailCacheService.ApplyLimitNow()
        End Sub

        ''' <summary>Wo die Vorschaubilder liegen, als Satz unter der Ordnerliste des Katalogs.</summary>
        Public ReadOnly Property ThumbnailCacheLocationSummary As String
            Get
                Return String.Format(LocalizationService.T("Die Vorschaubilder liegen in {0}."), ThumbnailCacheService.CurrentCacheRoot)
            End Get
        End Property

        Public Property ThumbnailCacheUsageText As String
            Get
                Return _thumbnailCacheUsageText
            End Get
            Private Set(value As String)
                Me.RaiseAndSetIfChanged(_thumbnailCacheUsageText, value)
            End Set
        End Property

        Private Sub LoadThumbnailCacheStorageSettings(settings As AppSettings)
            _thumbnailCacheLimitGigabytes = AppSettingsService.NormalizeThumbnailCacheLimitGigabytes(settings.ThumbnailCacheLimitGigabytes)
        End Sub

        Private Sub SnapshotThumbnailCacheStorageSettings()
            _savedThumbnailCacheLimitGigabytes = _thumbnailCacheLimitGigabytes
        End Sub

        Private Sub RestoreThumbnailCacheStorageSettings()
            ApplyThumbnailCacheLimit(_savedThumbnailCacheLimitGigabytes)
        End Sub

        Private Sub ResetThumbnailCacheStorageSettings()
            ApplyThumbnailCacheLimit(New AppSettings().ThumbnailCacheLimitGigabytes)
        End Sub

        ''' <summary>Den belegten Platz im Hintergrund messen. Laeuft beim Aufklappen der
        ''' Ordnerliste mit.</summary>
        Public Async Sub RefreshThumbnailCacheStorage()
            Try
                Dim size = Await Task.Run(Function() ThumbnailCacheService.MeasureCacheSizeBytes())
                ThumbnailCacheUsageText = String.Format(LocalizationService.T("Belegt: {0}"), FormatBytes(size))
                Me.RaisePropertyChanged(NameOf(ThumbnailCacheLocationSummary))
            Catch ex As Exception
                DiagnosticLogService.LogException("Settings.ThumbnailCacheStorage", ex)
            End Try
        End Sub

    End Class

End Namespace
