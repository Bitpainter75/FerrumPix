Imports System.Windows.Input
Imports Avalonia.Media
Imports ReactiveUI

Namespace ViewModels

    ''' <summary>Ein Etikett als Schalter im Suchdialog: ein Farbkreis, der mit Akzentring
    ''' angehakt ist. Die Palette ist dieselbe wie in der Infoleiste, damit man dort wie hier
    ''' dieselben Farben sieht.</summary>
    Public Class SearchLabelOption
        Inherits ReactiveObject

        Public Property Hex As String = ""
        Public Property Brush As IBrush
        Public Property ToolTip As String = ""
        Public Property ToggleCommand As ICommand

        Private _isActive As Boolean
        Public Property IsActive As Boolean
            Get
                Return _isActive
            End Get
            Set(value As Boolean)
                Me.RaiseAndSetIfChanged(_isActive, value)
            End Set
        End Property

    End Class

End Namespace
