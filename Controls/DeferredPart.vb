Imports Avalonia
Imports Avalonia.Controls
Imports Avalonia.Metadata

Namespace Controls

    ''' <summary>Ein Platzhalter, der seinen Inhalt erst baut, wenn er gebraucht wird, und ihn dann
    ''' behaelt.
    '''
    ''' WOZU. Eine Kachel der Galerie traegt fuenfzehn Knoepfe, von denen im Ruhezustand zwei zu
    ''' sehen sind: die uebrigen erscheinen erst beim Ueberfahren (Schnellaktionen, Sterne) oder
    ''' nur an manchen Bildern (Verworfen, Behalten, Bearbeitet, Abspielen, Stapel). Gebaut,
    ''' eingehaengt und angeordnet wurden trotzdem alle, und gemessen hing daran der groesste Teil
    ''' des ersten Aufbaus einer Kachel (rund 10 ms mit, 3,4 ms ohne Knoepfe).
    '''
    ''' WIE. Der Inhalt steht als Vorlage im Platzhalter (<see cref="PartTemplate"/>) und entsteht,
    ''' sobald <see cref="Load"/> zum ersten Mal wahr wird: ueber einen Stil (beim Ueberfahren der
    ''' Kachel) oder ueber dieselbe Bindung, die den Inhalt sichtbar macht. DANACH BLEIBT ER. Ihn
    ''' beim Verlassen wieder abzubauen war am 2026-08-28 der Fehler eines frueheren Versuchs:
    ''' beim Rollen mit dem Mausrad wandern die Kacheln unter dem Zeiger durch, und jede baute
    ''' ihre Aufsaetze auf und gleich wieder ab.
    '''
    ''' AM AUSSEHEN AENDERT ER NICHTS, solange nur aufgeschoben wird, was ohnehin unsichtbar ist
    ''' und keinen Platz belegt, auf den sich ein Nachbar stuetzt: der Platzhalter fuellt die Zelle
    ''' seines Elternteils, der Inhalt richtet sich darin aus wie zuvor. Die Stapelordnung
    ''' (Panel.ZIndex) gehoert an den Platzhalter, nicht an den Inhalt.</summary>
    Public Class DeferredPart
        Inherits Decorator

        Public Shared ReadOnly LoadProperty As StyledProperty(Of Boolean) =
            AvaloniaProperty.Register(Of DeferredPart, Boolean)(NameOf(Load))

        ''' <summary>Wahr: den Inhalt bauen. Ein spaeteres Falsch nimmt ihn nicht wieder weg.</summary>
        Public Property Load As Boolean
            Get
                Return GetValue(LoadProperty)
            End Get
            Set(value As Boolean)
                SetValue(LoadProperty, value)
            End Set
        End Property

        ''' <summary>Die Vorlage des Inhalts.</summary>
        <Content>
        Public Property PartTemplate As ITemplate(Of Control)
            Get
                Return _partTemplate
            End Get
            Set(value As ITemplate(Of Control))
                _partTemplate = value
                ' Die Vorlage kommt beim Aufbau NACH den Attributen: stand Load da schon auf wahr,
                ' gab es beim Setzen noch nichts zu bauen.
                If Load Then EnsureLoaded()
            End Set
        End Property

        Private _partTemplate As ITemplate(Of Control)

        ''' <summary>Ist der Inhalt schon gebaut?</summary>
        Public ReadOnly Property IsPartLoaded As Boolean
            Get
                Return Child IsNot Nothing
            End Get
        End Property

        ''' <summary>Baut den Inhalt jetzt, wenn er noch fehlt. Fuer Stellen, die ihn brauchen,
        ''' bevor der Zeiger da war (Tastatur, Pruefungen).</summary>
        Public Sub EnsureLoaded()
            If Child IsNot Nothing OrElse PartTemplate Is Nothing Then Return
            Dim built = PartTemplate.Build()
            If built Is Nothing Then Return
            Child = built
            ' Texte und Tooltips der Vorlage sind Schluessel der Uebersetzung; die Kachel selbst
            ' wurde beim Einhaengen uebersetzt, dieser Teil kommt erst jetzt dazu.
            Services.LocalizationService.ApplyToMaterialized(built)
        End Sub

        Protected Overrides Sub OnPropertyChanged(change As AvaloniaPropertyChangedEventArgs)
            MyBase.OnPropertyChanged(change)
            If change.Property Is LoadProperty AndAlso change.GetNewValue(Of Boolean)() Then EnsureLoaded()
        End Sub
    End Class

End Namespace
