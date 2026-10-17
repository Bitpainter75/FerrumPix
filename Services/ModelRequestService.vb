Imports System
Imports System.Collections.Generic
Imports System.Linq
Imports System.Threading.Tasks

Namespace Services

    ''' <summary>Der Zugang zur Rueckfrage "Modell herunterladen?" fuer Stellen ohne eigenen Weg
    ''' zum Hauptfenster, etwa das Infopanel. Belegt wird er einmal vom Hauptfenster mit
    ''' SettingsViewModel.RequestModelsAsync; dort wird gefragt und geholt.
    '''
    ''' Ohne Belegung (Pruefstand, Kommandozeile) wird nie gefragt und nie geholt: es zaehlt nur,
    ''' ob die Dateien schon da sind.</summary>
    Public NotInheritable Class ModelRequestService

        Private Sub New()
        End Sub

        Public Shared Property Handler As Func(Of String, IEnumerable(Of String), Task(Of Boolean))

        ''' <summary>True, wenn danach alle Dateien vorliegen. Der Download ist modal (siehe
        ''' SettingsViewModel.RequestModelsAsync), der Aufrufer darf danach also weitermachen.</summary>
        ''' <param name="featureName">Die Funktion, schon uebersetzt.</param>
        Public Shared Async Function RequestAsync(featureName As String, keys As IEnumerable(Of String)) As Task(Of Boolean)
            Dim list = keys.ToList()
            If AiModelService.RuntimeAvailable AndAlso
               list.All(Function(k) Not String.IsNullOrEmpty(AiModelService.BestFile(k))) Then Return True
            Dim ask = Handler
            If ask Is Nothing Then Return False
            Return Await ask(featureName, list)
        End Function

    End Class

End Namespace
