Imports System
Imports FerrumPix.Services

Namespace ViewModels

    ''' <summary>Die Zeile "Ordner für Anpassungen": wo die im Editor gespeicherten Anpassungen
    ''' als Dateien liegen (AdjustmentPresetStore). Waehlen laesst sich der Ort nicht; er liegt im
    ''' Datenordner und zieht mit ihm um (AppFolders).</summary>
    Partial Public Class SettingsViewModel

        ''' <summary>Der Ordner, wie er dasteht.</summary>
        Public ReadOnly Property AdjustmentPresetFolderText As String
            Get
                Return AdjustmentPresetStore.DefaultRoot
            End Get
        End Property

    End Class

End Namespace
