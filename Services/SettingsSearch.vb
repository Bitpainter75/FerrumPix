Imports System.Globalization
Imports Avalonia
Imports Avalonia.Controls
Imports Avalonia.Controls.Documents
Imports Avalonia.Data
Imports Avalonia.LogicalTree

Namespace Services

    ''' <summary>Die Suche der Einstellungen: blendet aus, was nicht trifft.
    '''
    ''' <para>GESUCHT WIRD IM ANGEZEIGTEN TEXT, nicht in einer eigenen Stichwortliste: Bezeichnung,
    ''' Erklaerung, Knopf- und Hakentexte, Platzhalter und Tooltips einer Zeile. Die Oberflaeche ist
    ''' zu diesem Zeitpunkt schon uebersetzt, die Suche geht also in jeder Sprache, ohne dass eine
    ''' Liste mitgepflegt werden muesste. Mehrere Woerter muessen alle in DERSELBEN Zeile stehen;
    ''' Gross- und Kleinschreibung und Akzente zaehlen nicht.</para>
    '''
    ''' <para>DER AUFBAU, auf den sie sich stuetzt: ein Abschnitt ist ein Border mit einer Spalte
    ''' darin. Ihre Kinder sind Zeilen (die Blaetter), Untergruppen (eine senkrechte Spalte, die
    ''' selbst wieder Zeilen traegt, etwa die Tastenkuerzel), Zwischenueberschriften (ein einzelner
    ''' TextBlock) und Trennlinien. Das erste reine Textstueck einer Spalte ist ihre Ueberschrift; sie
    ''' bleibt stehen, solange darunter etwas trifft. Trifft der TITEL einer Untergruppe oder eine
    ''' Zwischenueberschrift selbst, bleibt alles darunter ungefiltert stehen. Ein Abschnitt, dessen
    ''' Eintrag in der Bereichsliste trifft, bleibt ganz stehen.</para>
    '''
    ''' <para>AUSGEBLENDET WIRD MIT HOEHERER PRIORITAET und danach zurueckgegeben, nicht ueberschrieben:
    ''' viele Zeilen haben ein gebundenes IsVisible (nur mit Modellen, nur unter Windows). Ein
    ''' lokal gesetztes False haette die Bindung ersetzt, und die Zeile waere nach der Suche auch
    ''' dort weg geblieben, wo sie hingehoert. Die Suche blendet deshalb nur AUS, nie ein - was die
    ''' Bindung verbirgt, bleibt verborgen.</para></summary>
    Public NotInheritable Class SettingsSearch

        Private ReadOnly _hidden As New List(Of IDisposable)()

        ''' <summary>Filtert die Abschnitte. Rueckgabe: wie viele Abschnitte etwas zeigen, oder -1
        ''' bei leerer Suche (dann ist alles wieder wie vorher).</summary>
        ''' <param name="sections">Die Abschnitte, jeder ein Border um eine Spalte.</param>
        ''' <param name="navigation">Zu jedem Abschnitt sein Eintrag in der Bereichsliste, oder
        ''' Nothing.</param>
        Public Function Apply(sections As IEnumerable(Of Border), navigation As Func(Of Border, Control), query As String) As Integer
            Clear()
            Dim words = SplitWords(query)
            If words.Length = 0 Then Return -1

            Dim shown = 0
            For Each section In sections
                If section Is Nothing OrElse Not section.IsVisible Then Continue For
                Dim nav = navigation?.Invoke(section)
                Dim hit As Boolean
                If nav IsNot Nothing AndAlso Matches(nav, words) Then
                    hit = True
                Else
                    Dim column = TryCast(section.Child, Panel)
                    hit = column IsNot Nothing AndAlso FilterColumn(column, words)
                End If
                If hit Then
                    shown += 1
                Else
                    Hide(section)
                    If nav IsNot Nothing Then Hide(nav)
                End If
            Next
            Return shown
        End Function

        ''' <summary>Gibt alles Ausgeblendete zurueck.</summary>
        Public Sub Clear()
            For Each handle In _hidden
                handle.Dispose()
            Next
            _hidden.Clear()
        End Sub

        Private Sub Hide(control As Control)
            Dim handle = control.SetValue(Visual.IsVisibleProperty, False, BindingPriority.Animation)
            If handle IsNot Nothing Then _hidden.Add(handle)
        End Sub

        ''' <summary>Filtert die Kinder einer Spalte; True, wenn darin etwas trifft.</summary>
        Private Function FilterColumn(column As Panel, words As String()) As Boolean
            Dim children = column.Children.ToList()
            Dim header = children.FirstOrDefault(Function(c) IsTextOnly(c) AndAlso Not TypeOf c Is TextBlock)

            ' Zwischenueberschriften teilen die Spalte in Gruppen; was vor der ersten steht, gehoert
            ' zu keiner.
            Dim anyHit = False
            Dim heading As TextBlock = Nothing
            Dim headingMatches = False
            Dim groupHit = False

            Dim closeGroup = Sub()
                                 If heading IsNot Nothing AndAlso Not groupHit Then Hide(heading)
                                 If groupHit Then anyHit = True
                             End Sub

            For Each child In children
                If Object.ReferenceEquals(child, header) Then Continue For
                Dim subHeading = TryCast(child, TextBlock)
                If subHeading IsNot Nothing Then
                    closeGroup()
                    heading = subHeading
                    headingMatches = Matches(subHeading, words)
                    groupHit = headingMatches
                    Continue For
                End If
                If IsDecoration(child) Then
                    Hide(child)
                    Continue For
                End If
                If headingMatches Then Continue For

                Dim hit As Boolean
                Dim group = TryCast(child, StackPanel)
                If group IsNot Nothing AndAlso IsGroup(group) Then
                    hit = FilterGroup(group, words)
                Else
                    hit = Matches(child, words)
                    If Not hit Then Hide(child)
                End If
                If hit Then
                    If heading IsNot Nothing Then groupHit = True Else anyHit = True
                End If
            Next
            closeGroup()

            If header IsNot Nothing AndAlso Not anyHit Then Hide(header)
            Return anyHit
        End Function

        ''' <summary>Eine Untergruppe: trifft ihr Titel, bleibt sie ganz; sonst wird sie wie eine
        ''' Spalte gefiltert und verschwindet, wenn nichts darin trifft.</summary>
        Private Function FilterGroup(group As StackPanel, words As String()) As Boolean
            Dim header = group.Children.FirstOrDefault(Function(c) IsTextOnly(c))
            If header IsNot Nothing AndAlso Matches(TitleOf(header), words) Then Return True
            Dim hit = FilterColumn(group, words)
            If Not hit Then Hide(group)
            Return hit
        End Function

        ''' <summary>Eine senkrechte Spalte, die selbst Zeilen traegt - nicht der linke Teil einer
        ''' Zeile, der nur aus Bezeichnung und Erklaerung besteht.</summary>
        Private Shared Function IsGroup(panel As StackPanel) As Boolean
            If panel.Orientation <> Layout.Orientation.Vertical Then Return False
            Return panel.Children.Any(Function(c) TypeOf c Is Panel)
        End Function

        ''' <summary>Ein einzelner TextBlock oder eine Spalte nur aus TextBlocks (Titel und
        ''' Erklaerung).</summary>
        Private Shared Function IsTextOnly(control As Control) As Boolean
            If TypeOf control Is TextBlock Then Return True
            Dim panel = TryCast(control, StackPanel)
            Return panel IsNot Nothing AndAlso panel.Children.Count > 0 AndAlso
                   panel.Children.All(Function(c) TypeOf c Is TextBlock)
        End Function

        ''' <summary>Trennlinien: ohne Text, ohne Bedienung, im Suchergebnis nur Luecken.</summary>
        Private Shared Function IsDecoration(control As Control) As Boolean
            If TypeOf control Is Separator Then Return True
            Dim border = TryCast(control, Border)
            Return border IsNot Nothing AndAlso border.Child Is Nothing
        End Function

        ''' <summary>Der Titel eines Textstuecks: der erste TextBlock darin.</summary>
        Private Shared Function TitleOf(control As Control) As Control
            Dim panel = TryCast(control, Panel)
            If panel Is Nothing Then Return control
            Return If(panel.Children.FirstOrDefault(), control)
        End Function

        Private Shared Function SplitWords(query As String) As String()
            If String.IsNullOrWhiteSpace(query) Then Return Array.Empty(Of String)()
            Return query.Split(New Char() {" "c, ChrW(9)}, StringSplitOptions.RemoveEmptyEntries)
        End Function

        Private Shared ReadOnly Comparer As CompareInfo = CultureInfo.InvariantCulture.CompareInfo
        Private Const CompareMode As CompareOptions = CompareOptions.IgnoreCase Or CompareOptions.IgnoreNonSpace

        Private Shared Function Matches(control As Control, words As String()) As Boolean
            Dim text = CollectText(control)
            For Each word In words
                If Comparer.IndexOf(text, word, CompareMode) < 0 Then Return False
            Next
            Return True
        End Function

        ''' <summary>Aller sichtbare Text eines Teilbaums, eine Zeile je Stueck.</summary>
        Friend Shared Function CollectText(root As Control) As String
            Dim parts As New List(Of String)()
            For Each node In root.GetSelfAndLogicalDescendants()
                Dim control = TryCast(node, Control)
                If control Is Nothing Then Continue For
                Dim tip = TryCast(ToolTip.GetTip(control), String)
                If Not String.IsNullOrEmpty(tip) Then parts.Add(tip)

                Dim textBlock = TryCast(control, TextBlock)
                If textBlock IsNot Nothing Then
                    If Not String.IsNullOrEmpty(textBlock.Text) Then
                        parts.Add(textBlock.Text)
                    ElseIf textBlock.Inlines IsNot Nothing Then
                        parts.Add(textBlock.Inlines.Text)
                    End If
                    Continue For
                End If
                Dim textBox = TryCast(control, TextBox)
                If textBox IsNot Nothing Then
                    If Not String.IsNullOrEmpty(textBox.PlaceholderText) Then parts.Add(textBox.PlaceholderText)
                    Continue For
                End If
                Dim content = TryCast(TryCast(control, ContentControl)?.Content, String)
                If Not String.IsNullOrEmpty(content) Then parts.Add(content)
                Dim header = TryCast(TryCast(control, Avalonia.Controls.Primitives.HeaderedContentControl)?.Header, String)
                If Not String.IsNullOrEmpty(header) Then parts.Add(header)
            Next
            Return String.Join(vbLf, parts)
        End Function

    End Class

End Namespace
