Imports System
Imports System.Windows.Input
Imports FerrumPix.Services
Imports ReactiveUI

Namespace ViewModels

    ''' <summary>Eine Zeile der Liste "Zielordner für den Import" in den Einstellungen: Name und
    ''' Vorlage mit Platzhaltern. Jede Aenderung geht sofort in die Einstellungen, wie bei den
    ''' externen Programmen; die Textfelder melden erst beim Verlassen. Dieselbe Zeile traegt die
    ''' Liste "Dateinamen für den Import"; dann zeigt das Beispiel einen Dateinamen.</summary>
    Public Class ImportTargetRow
        Inherits ReactiveObject

        Private ReadOnly _id As String
        Private ReadOnly _save As Action
        Private ReadOnly _isFileName As Boolean
        Private ReadOnly _sampleFolder As String
        Private _name As String
        Private _template As String

        ''' <param name="sampleFolder">Fuer die Listen von Speichern unter: der Ordner, den das
        ''' Beispiel fuer {folder} und {foldername} einsetzt. Nothing bei den Listen des Imports.</param>
        Public Sub New(target As ImportTargetSettings, save As Action, remove As Action(Of ImportTargetRow),
                       Optional isFileName As Boolean = False, Optional sampleFolder As String = Nothing)
            _id = If(String.IsNullOrWhiteSpace(target?.Id), Guid.NewGuid().ToString("N"), target.Id)
            _name = If(target?.Name, "")
            _template = If(target?.Template, "")
            _save = save
            _isFileName = isFileName
            _sampleFolder = sampleFolder
            RemoveCommand = ReactiveCommand.Create(Sub() remove?.Invoke(Me))
        End Sub

        Public Property Name As String
            Get
                Return _name
            End Get
            Set(value As String)
                Dim cleaned = If(value, "").Trim()
                If cleaned = _name Then Return
                Me.RaiseAndSetIfChanged(_name, cleaned)
                _save?.Invoke()
            End Set
        End Property

        Public Property Template As String
            Get
                Return _template
            End Get
            Set(value As String)
                Dim cleaned = If(value, "").Trim()
                If cleaned = _template Then Return
                Me.RaiseAndSetIfChanged(_template, cleaned)
                Me.RaisePropertyChanged(NameOf(PreviewText))
                _save?.Invoke()
            End Set
        End Property

        ''' <summary>Wie die Vorlage fuer eine Aufnahme von heute aufgeloest wuerde; ein Dateiname
        ''' fuer die erste Datei eines Laufs mit dem Auftrag "Auftrag".</summary>
        Public ReadOnly Property PreviewText As String
            Get
                If String.IsNullOrWhiteSpace(_template) Then Return ""
                Dim make = LocalizationService.T("Hersteller")
                Dim model = LocalizationService.T("Modell")
                Dim sample As New ImportCandidate With {.CapturedAt = DateTime.Now, .ModifiedAt = DateTime.Now, .Camera = make & " " & model,
                                                        .CameraMake = make, .CameraModel = model,
                                                        .SourcePath = "IMG_0001.CR3"}
                Dim template = _template
                If _sampleFolder IsNot Nothing Then
                    template = template.Replace("{folder}", _sampleFolder).
                                        Replace("{foldername}", IO.Path.GetFileName(_sampleFolder.TrimEnd("/"c, "\"c)))
                End If
                If _isFileName Then
                    Return String.Format(LocalizationService.T("Beispiel: {0}"),
                                         CardImportService.TargetFileNameFor(template, sample, LocalizationService.T("Auftrag"), 1))
                End If
                Return String.Format(LocalizationService.T("Beispiel: {0}"), CardImportService.ExpandTargetTemplate(template, sample))
            End Get
        End Property

        Public ReadOnly Property RemoveCommand As ICommand

        Public Function ToSettings() As ImportTargetSettings
            Return New ImportTargetSettings With {.Id = _id, .Name = _name, .Template = _template}
        End Function

    End Class

End Namespace
