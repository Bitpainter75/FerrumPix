Imports ReactiveUI
Imports FerrumPix.Services

Namespace ViewModels

    ''' <summary>
    ''' Eine Vorgabenkachel im Dialog „Neues Bild".
    '''
    ''' Warum es diese Hülle um <see cref="DocumentPresetService.DocumentPreset"/> gibt: die Vorgaben
    ''' selbst sind eine gemeinsame, statische Tabelle. Die Markierung „gerade gewählt" ist dagegen
    ''' Zustand DIESES Dialogs und hat in der Tabelle nichts verloren - sonst schriebe die Ansicht in
    ''' gemeinsam genutzte Daten. Die Alternative wäre ein Konverter mit dynamischem Parameter
    ''' gewesen, den Avalonia nur über MultiBinding-Elementsyntax hergibt.
    ''' </summary>
    Public Class NewDocPresetItem
        Inherits ViewModelBase

        Private _isSelected As Boolean

        Public Sub New(preset As DocumentPresetService.DocumentPreset)
            Me.Preset = preset
        End Sub

        Public ReadOnly Property Preset As DocumentPresetService.DocumentPreset

        Public ReadOnly Property Id As String
            Get
                Return Preset.Id
            End Get
        End Property

        ''' <summary>Deutscher Quelltext - die Ansicht übersetzt ihn beim Baumdurchlauf.</summary>
        Public ReadOnly Property Label As String
            Get
                Return Preset.Label
            End Get
        End Property

        Public ReadOnly Property SizeText As String
            Get
                Return Preset.SizeText
            End Get
        End Property

        Public Property IsSelected As Boolean
            Get
                Return _isSelected
            End Get
            Set(value As Boolean)
                Me.RaiseAndSetIfChanged(_isSelected, value)
            End Set
        End Property

    End Class

End Namespace
