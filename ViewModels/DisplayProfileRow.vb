Imports System
Imports System.Collections.Generic
Imports System.IO
Imports System.Windows.Input
Imports FerrumPix.Services
Imports ReactiveUI

Namespace ViewModels

    ''' <summary>Eine Zeile der Bildschirmprofile in den Einstellungen: ein erkannter Bildschirm
    ''' und das ICC-Profil, das für ihn gilt. Unveränderlich; wer ein Profil wählt oder entfernt,
    ''' bekommt eine neu aufgebaute Liste (<see cref="SettingsViewModel.RefreshDisplayProfiles"/>).</summary>
    Public Class DisplayProfileRow
        Inherits ReactiveObject

        Public Sub New(screenKey As String, width As Integer, height As Integer, isPrimary As Boolean,
                       isCurrent As Boolean, profilePath As String, remove As Action(Of DisplayProfileRow))
            Me.ScreenKey = screenKey
            ProfilePath = If(profilePath, "")
            Dim parts As New List(Of String) From {
                String.Format(LocalizationService.T("{0} x {1} Bildpunkte"), width, height)
            }
            If isPrimary Then parts.Add(LocalizationService.T("Hauptbildschirm"))
            If isCurrent Then parts.Add(LocalizationService.T("Das Fenster steht hier"))
            Detail = String.Join(", ", parts)

            If ProfilePath.Length = 0 Then
                ProfileText = LocalizationService.T("Kein Profil")
            Else
                ProfileText = Path.GetFileName(ProfilePath)
                Dim loaded = DisplayProfileService.Load(ProfilePath)
                StatusText = If(loaded.Filter Is Nothing, loaded.ErrorText, "")
            End If
            RemoveCommand = ReactiveCommand.Create(Sub() remove?.Invoke(Me))
        End Sub

        ''' <summary>Name des Bildschirms, wie das System ihn meldet (unter X11 etwa "DP-1"); zugleich
        ''' der Schlüssel in den Einstellungen.</summary>
        Public ReadOnly Property ScreenKey As String
        Public ReadOnly Property Detail As String
        Public ReadOnly Property ProfilePath As String
        Public ReadOnly Property ProfileText As String
        ''' <summary>Warum das Profil nicht wirkt; leer, wenn es wirkt oder keines gewählt ist.</summary>
        Public ReadOnly Property StatusText As String = ""

        Public ReadOnly Property HasProfile As Boolean
            Get
                Return ProfilePath.Length > 0
            End Get
        End Property

        Public ReadOnly Property HasStatus As Boolean
            Get
                Return StatusText.Length > 0
            End Get
        End Property

        Public ReadOnly Property RemoveCommand As ICommand
    End Class

End Namespace
