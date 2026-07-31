Imports System
Imports System.Globalization
Imports System.Resources
Imports System.Runtime.CompilerServices
Imports System.Security.Cryptography
Imports System.Text
Imports System.Text.RegularExpressions
Imports Avalonia
Imports Avalonia.Controls
Imports Avalonia.Controls.Primitives
Imports Avalonia.Input
Imports Avalonia.LogicalTree
Imports Avalonia.VisualTree

Namespace Services

    Public NotInheritable Class LocalizationService
        Private Sub New()
        End Sub

        Public Shared Event LanguageChanged As EventHandler

        Private Shared _languageMode As String = "System"
        Private Shared ReadOnly Strings As New ResourceManager("FerrumPix.Strings", GetType(LocalizationService).Assembly)
        ''' Der URSPRUNGStext je Anzeige, schwach referenziert. Ohne ihn liest der Durchlauf den
        ''' ANGEZEIGTEN Text als Quelle: nach dem ersten Sprachwechsel steht dort die Uebersetzung,
        ''' und die zweite Umschaltung sucht einen Schluessel, den es nicht gibt - die Anzeige bleibt
        ''' dann in der zuvor gewaehlten Sprache stehen.
        Private Shared ReadOnly Ursprungstexte As New ConditionalWeakTable(Of ILogical, NodeTexts)()
        ''' Eigene Ressourcendatei nur für die Such-Tags der Formen/Symbole-Icons (Resources/IconTags*.resx),
        ''' getrennt von den allgemeinen UI-Texten, damit sich beide unabhängig voneinander pflegen lassen.
        Private Shared ReadOnly IconTags As New ResourceManager("FerrumPix.IconTags", GetType(LocalizationService).Assembly)

        Public Shared Property LanguageMode As String
            Get
                Return _languageMode
            End Get
            Set(value As String)
                Dim normalized = NormalizeLanguageMode(value)
                If _languageMode = normalized Then Return
                _languageMode = normalized
                RaiseEvent LanguageChanged(Nothing, EventArgs.Empty)
            End Set
        End Property

        Public Shared ReadOnly Property EffectiveCulture As CultureInfo
            Get
                Dim code = ResolveCultureCode(_languageMode)
                If String.IsNullOrEmpty(code) Then Return CultureInfo.InvariantCulture
                Return CultureInfo.GetCultureInfo(code)
            End Get
        End Property

        Public Shared ReadOnly Property EffectiveLanguage As String
            Get
                Select Case EffectiveCulture.TwoLetterISOLanguageName
                    Case "de" : Return "German"
                    Case "es" : Return "Spanish"
                    Case "fr" : Return "French"
                    Case "it" : Return "Italian"
                    Case "pt" : Return "Portuguese"
                    Case "zh" : Return "Chinese"
                    Case Else : Return "English"
                End Select
            End Get
        End Property

        ''' <summary>Die waehlbaren Sprachen, mit ihrem Namen in der jeweiligen Sprache SELBST.
        '''
        ''' EINE Liste, aus der sich alles andere ergibt: die Auswahlliste im Einstellungsdialog, die
        ''' Normierung und der Waechter. Eine weitere Sprache ist damit ein Eintrag hier plus die
        ''' resx-Dateien - und keine weitere Knopfreihe, die mit jeder Sprache unhaltbarer wird.
        '''
        ''' Die Namen stehen bewusst in ihrer eigenen Sprache und werden NICHT uebersetzt. Wer die
        ''' Oberflaeche in einer Sprache sieht, die er nicht versteht, findet "Deutsch" wieder -
        ''' "Tedesco" nicht.</summary>
        Public Shared ReadOnly Property Languages As (Key As String, Name As String)()
            Get
                Return {
                    ("System", ""),
                    ("German", "Deutsch"),
                    ("English", "English"),
                    ("Spanish", "Español"),
                    ("French", "Français"),
                    ("Italian", "Italiano"),
                    ("Portuguese", "Português"),
                    ("Chinese", "简体中文")}
            End Get
        End Property

        Public Shared Function NormalizeLanguageMode(value As String) As String
            Dim normalized = If(value, "").Trim()
            For Each sprache In Languages
                If sprache.Key <> "System" AndAlso
                   String.Equals(sprache.Key, normalized, StringComparison.Ordinal) Then
                    Return sprache.Key
                End If
            Next
            Return "System"
        End Function

        Public Shared Function T(text As String) As String
            If String.IsNullOrEmpty(text) Then Return text

            Try
                Dim key = MakeKey(text)
                Dim translated = Strings.GetString(key, EffectiveCulture)
                If Not String.IsNullOrEmpty(translated) Then Return translated

                translated = Strings.GetString(key, CultureInfo.InvariantCulture)
                Return If(String.IsNullOrEmpty(translated), text, translated)
            Catch ex As MissingManifestResourceException
                Return text
            End Try
        End Function

        ''' Übersetzt einen Icon-Such-Tag (z.B. den aus dem SVG-Dateinamen abgeleiteten Namen "Stern")
        ''' über die dedizierte IconTags-Ressource. Fällt wie T auf den Ausgangstext zurück, wenn
        ''' für den Tag noch keine Übersetzung gepflegt wurde.
        Public Shared Function Tag(text As String) As String
            If String.IsNullOrEmpty(text) Then Return text

            Try
                Dim key = MakeTagKey(text)
                Dim translated = IconTags.GetString(key, EffectiveCulture)
                If Not String.IsNullOrEmpty(translated) Then Return translated

                translated = IconTags.GetString(key, CultureInfo.InvariantCulture)
                Return If(String.IsNullOrEmpty(translated), text, translated)
            Catch ex As MissingManifestResourceException
                Return text
            End Try
        End Function

        Private Shared Function MakeTagKey(text As String) As String
            Dim baseName = Regex.Replace(text, "[^A-Za-z0-9]+", "_").Trim("_"c)
            If baseName.Length = 0 Then baseName = "Tag"
            Return baseName.Substring(0, Math.Min(baseName.Length, 64))
        End Function

        Public Shared Sub ApplyTo(root As ILogical)
            If root Is Nothing Then Return
            ApplyOne(root)
            For Each child In root.GetLogicalChildren()
                ApplyTo(child)
            Next
        End Sub

        ''' <summary>Lokalisiert einen bereits materialisierten SICHTbaum. Inhalte aus DataTemplates -
        ''' Flyouts, Galerie-Kacheln - liegen nicht vollstaendig im logischen Baum; ohne diesen
        ''' Einstieg blieben sie deutsch, obwohl der Durchlauf ueber das Fenster lief.</summary>
        Public Shared Sub ApplyToVisualTree(root As Visual)
            If root Is Nothing Then Return
            Dim logical = TryCast(root, ILogical)
            If logical IsNot Nothing Then ApplyOne(logical)
            For Each child In root.GetVisualChildren()
                ApplyToVisualTree(child)
            Next
        End Sub

        Private Shared Function ResolveCultureCode(mode As String) As String
            Select Case NormalizeLanguageMode(mode)
                Case "German" : Return "de"
                Case "Spanish" : Return "es"
                Case "French" : Return "fr"
                Case "Italian" : Return "it"
                Case "Portuguese" : Return "pt"
                ' Chinesisch braucht die REGION: die Ressourcen liegen als zh-CN (vereinfacht), und
                ' ein blosses "zh" wuerde sie nicht finden.
                Case "Chinese" : Return "zh-CN"
                Case "English" : Return ""
                Case Else
                    Return ResolveSystemCultureCode()
            End Select
        End Function

        Private Shared Function ResolveSystemCultureCode() As String
            Dim systemCode = CultureInfo.CurrentUICulture.TwoLetterISOLanguageName.ToLowerInvariant()
            If IsSupportedCultureCode(systemCode) Then Return MitRegion(systemCode)

            For Each variableName In {"LANGUAGE", "LC_MESSAGES", "LANG"}
                Dim code = ExtractCultureCode(Environment.GetEnvironmentVariable(variableName))
                If IsSupportedCultureCode(code) Then Return MitRegion(code)
            Next

            Return ""
        End Function

        Private Shared Function ExtractCultureCode(value As String) As String
            value = If(value, "").Trim()
            If String.IsNullOrEmpty(value) Then Return ""

            Dim first = value.Split(":"c)(0).Trim()
            If String.Equals(first, "C", StringComparison.OrdinalIgnoreCase) OrElse
               String.Equals(first, "POSIX", StringComparison.OrdinalIgnoreCase) Then Return ""

            Dim normalized = first.Split("."c)(0).Split("@"c)(0).Replace("-"c, "_"c)
            Dim separator = normalized.IndexOf("_"c)
            If separator >= 0 Then normalized = normalized.Substring(0, separator)
            Return normalized.ToLowerInvariant()
        End Function

        Private Shared Function IsSupportedCultureCode(code As String) As Boolean
            Select Case If(code, "").ToLowerInvariant()
                Case "de", "es", "fr", "it", "pt", "zh"
                    Return True
                Case Else
                    Return False
            End Select
        End Function

        ''' <summary>Der Ressourcenname zu einem Zweibuchstaben-Code. Nur Chinesisch braucht eine
        ''' Region: die Dateien heissen zh-CN, ein blosses "zh" findet sie nicht.</summary>
        Private Shared Function MitRegion(code As String) As String
            If String.Equals(code, "zh", StringComparison.OrdinalIgnoreCase) Then Return "zh-CN"
            Return code
        End Function

        Private Shared Function MakeKey(text As String) As String
            Dim baseName = Regex.Replace(text, "[^A-Za-z0-9]+", "_").Trim("_"c)
            If baseName.Length = 0 Then baseName = "Text"
            If baseName.Length > 48 Then baseName = baseName.Substring(0, 48)

            Using sha = SHA1.Create()
                Dim hashBytes = sha.ComputeHash(Encoding.UTF8.GetBytes(text))
                Dim hash = BitConverter.ToString(hashBytes).Replace("-", "").Substring(0, 8).ToLowerInvariant()
                Return $"{baseName}_{hash}"
            End Using
        End Function

        ''' <summary>Ein Text mit dieser Klasse wird NICHT uebersetzt. Gedacht fuer Anzeigen, deren
        ''' Inhalt aus einer Bindung kommt: das Zuweisen von Text loescht die Bindung, und die Anzeige
        ''' steht danach fuer immer auf dem Wert, den sie beim Sprachwechsel zufaellig hatte.
        '''
        ''' Warum das lange nicht auffiel: der Baumdurchlauf ueberspringt LEERE Texte, und die
        ''' allermeisten gebundenen Anzeigen sind beim Anwenden der Sprache noch leer. Erst eine, die
        ''' schon beim Aufbau etwas anzeigt, faellt in die Falle.</summary>
        Public Const KeineUebersetzung As String = "no-translate"

        Private Shared Sub ApplyOne(node As ILogical)
            Dim merker = Ursprungstexte.GetValue(node, Function(ignoriert) New NodeTexts())
            Dim textBlock = TryCast(node, TextBlock)
            If textBlock IsNot Nothing AndAlso Not String.IsNullOrEmpty(textBlock.Text) AndAlso
               Not textBlock.Classes.Contains(KeineUebersetzung) Then
                textBlock.Text = TranslateRemembered(textBlock.Text, merker.Text)
            End If

            Dim content = TryCast(node, ContentControl)
            If content IsNot Nothing AndAlso TypeOf content.Content Is String Then
                content.Content = TranslateRemembered(CStr(content.Content), merker.Inhalt)
            End If

            Dim menuItem = TryCast(node, MenuItem)
            If menuItem IsNot Nothing AndAlso TypeOf menuItem.Header Is String Then
                menuItem.Header = TranslateRemembered(CStr(menuItem.Header), merker.MenueKopf)
            End If

            ' Expander und TabItem erben Header nicht von MenuItem, sondern von
            ' HeaderedContentControl - ohne diesen Zweig blieben ihre Überschriften deutsch.
            Dim headered = TryCast(node, HeaderedContentControl)
            If headered IsNot Nothing AndAlso TypeOf headered.Header Is String Then
                headered.Header = TranslateRemembered(CStr(headered.Header), merker.Kopf)
            End If

            Dim textBox = TryCast(node, TextBox)
            If textBox IsNot Nothing AndAlso Not String.IsNullOrEmpty(textBox.PlaceholderText) Then
                textBox.PlaceholderText = TranslateRemembered(textBox.PlaceholderText, merker.Platzhalter)
            End If

            ' AutoCompleteBox ist keine TextBox, hat aber denselben Platzhalter.
            Dim autoComplete = TryCast(node, AutoCompleteBox)
            If autoComplete IsNot Nothing AndAlso Not String.IsNullOrEmpty(autoComplete.PlaceholderText) Then
                autoComplete.PlaceholderText = TranslateRemembered(autoComplete.PlaceholderText, merker.Platzhalter)
            End If

            ' ComboBox erbt ihren Platzhalter ebenfalls nicht von TextBox - ohne diesen Zweig bliebe der
            ' Text einer noch leeren Auswahlliste deutsch.
            Dim comboBox = TryCast(node, ComboBox)
            If comboBox IsNot Nothing AndAlso Not String.IsNullOrEmpty(comboBox.PlaceholderText) Then
                comboBox.PlaceholderText = TranslateRemembered(comboBox.PlaceholderText, merker.Platzhalter)
            End If

            Dim control = TryCast(node, Control)
            If control IsNot Nothing Then
                Dim tip = ToolTip.GetTip(control)
                If TypeOf tip Is String Then
                    ToolTip.SetTip(control, TranslateRemembered(CStr(tip), merker.Tipp, appendSpace:=True))
                End If
            End If
        End Sub

        ''' <summary>Uebersetzt gegen den GEMERKTEN Ursprungstext. Weicht der aktuelle Text von dem
        ''' ab, was zuletzt gesetzt wurde, hat ihn jemand anders geschrieben - dann ist er die neue
        ''' Quelle.</summary>
        ''' <param name="appendSpace">Ein Leerzeichen ans Ende. NUR fuer Kurzhinweise.
        '''
        ''' Ein Kurzhinweis liegt in einem Aufklappfenster, und das wird in ganzen Gerätepunkten
        ''' angelegt. Bei krummer Anwendungsskalierung (1,72 heisst 0,58 Punkte je Geraetepunkt)
        ''' fehlt dem Text beim Anordnen ein Bruchteil, den die Messung noch hatte: ohne Umbruch
        ''' verschwand das letzte ZEICHEN, mit Umbruch das ganze letzte WORT auf einer zweiten
        ''' Zeile, die nicht mehr gezeigt wird.
        '''
        ''' Das Leerzeichen wird MITGEMESSEN und ist genau der Puffer, der den Bruchteil auffaengt.
        ''' Sichtbar ist es nicht. Es hier anzuhaengen und nicht an die Quelltexte ist Absicht: der
        ''' Schluessel wird aus dem deutschen Quelltext berechnet, ein Leerzeichen darin wuerde
        ''' JEDEN Kurzhinweis-Schluessel aendern.</param>
        Private Shared Function TranslateRemembered(current As String, merker As TextMerker,
                                                  Optional appendSpace As Boolean = False) As String
            If merker.Quelle Is Nothing OrElse
               Not String.Equals(current, merker.Zuletzt, StringComparison.Ordinal) Then
                ' Der gemerkte Ursprung darf den Puffer NICHT enthalten, sonst faende der zweite
                ' Durchlauf keinen Schluessel mehr und haengte bei jedem Sprachwechsel eins an.
                merker.Quelle = If(appendSpace, If(current, "").TrimEnd(), current)
            End If
            Dim uebersetzt = T(merker.Quelle)
            If appendSpace Then uebersetzt &= " "
            merker.Zuletzt = uebersetzt
            Return uebersetzt
        End Function

        Private NotInheritable Class NodeTexts
            Public ReadOnly Text As New TextMerker()
            Public ReadOnly Inhalt As New TextMerker()
            Public ReadOnly MenueKopf As New TextMerker()
            Public ReadOnly Kopf As New TextMerker()
            Public ReadOnly Platzhalter As New TextMerker()
            Public ReadOnly Tipp As New TextMerker()
        End Class

        Private NotInheritable Class TextMerker
            Public Quelle As String
            Public Zuletzt As String
        End Class
    End Class

End Namespace
