Imports System.Collections.Generic
Imports System.Linq
Imports Avalonia

Namespace Services

    ''' <summary>
    ''' Setzt zur Laufzeit die Abstands- und Hoehenwerte von Werkzeugleiste und Anpassungspanel auf
    ''' die Stufe, die der Nutzer gewaehlt hat.
    '''
    ''' Das Verfahren ist das von FontScaleService: ein Wert im Ressourcenwoerterbuch der Anwendung
    ''' gewinnt gegen jede DynamicResource-Nachschlage, also auch gegen die Regel, die ihn setzt.
    ''' Deshalb brauchen die betroffenen Regeln im Theme keinen Code - sie verweisen nur auf den
    ''' Schluessel ohne Stufe.
    '''
    ''' Zwei getrennte Eingriffe, weil beide Flaechen unabhaengig voneinander eng gestellt werden
    ''' koennen: ApplyTool aendert nur die Werkzeuge, ApplyAdjustments nur das Anpassungspanel. Die
    ''' Breite ist uebrigens nicht hier aufgehoben, sondern bei den Eigenschaften, die sie schon
    ''' vorher gehalten haben (EditorViewModel.ToolSidebarWidth und AdjustmentsPanelWidth).
    '''
    ''' Die Zahlen stehen ausschliesslich im Theme: der Schluessel ohne Stufe traegt die Norm, einer
    ''' mit Stufe (.compact, .tiny) die Variante. Nicht jede Stufe hat jeden Schluessel - fehlt
    ''' eine Variante, gilt die Norm, und genau das ist bei Tool.ButtonPadding der Fall, weil
    ''' compact an der Waagerechten nichts aendert.
    ''' </summary>
    Public NotInheritable Class PanelDensityService

        Private Sub New()
        End Sub

        ''' Die Masse der linken Werkzeugleiste.
        Private Shared ReadOnly ToolKeys As String() = {
            "FP.Density.Tool.ButtonHeight",
            "FP.Density.Tool.ButtonPadding",
            "FP.Density.Tool.ItemSpacing",
            "FP.Density.Tool.HeaderMargin",
            "FP.Density.Tool.SidebarPadding"
        }

        ''' Die Masse des Anpassungspanels.
        Private Shared ReadOnly AdjustmentsKeys As String() = {
            "FP.Density.Adjust.GroupPadding",
            "FP.Density.Adjust.GroupMargin",
            "FP.Density.Adjust.ContentMargin",
            "FP.Density.Adjust.SliderHeight"
        }

        ''' Die Werte des Themes, wie sie vor dem ersten Eingriff dastehen - also die Norm.
        '''
        ''' Einmalig gelesen, bevor der erste Tausch sie ueberschreibt; danach liegt die Norm nur noch
        ''' hier. Beide Listen werden auf einmal gelesen, obwohl zunachst meist nur eine gebraucht
        ''' wird: die zweite Koennte sonst ihre Norm nur noch in einem bereits getauschten Wert
        ''' finden.
        Private Shared _base As Dictionary(Of String, Object)

        ''' <summary>Werkzeugleiste auf eine Dichtestufe setzen. Der Wert kommt schon vorbereitet aus
        ''' AppSettingsService.NormalizePanelDensity, ein unbekannter also als normal.</summary>
        Public Shared Sub ApplyTool(mode As String)
            Dim app = Application.Current
            If app Is Nothing Then Return
            EnsureBase(app)
            ApplyTo(app, ToolKeys, AppSettingsService.NormalizePanelDensity(mode))
        End Sub

        ''' <summary>Anpassungspanel auf eine Dichtestufe setzen.</summary>
        Public Shared Sub ApplyAdjustments(mode As String)
            Dim app = Application.Current
            If app Is Nothing Then Return
            EnsureBase(app)
            ApplyTo(app, AdjustmentsKeys, AppSettingsService.NormalizePanelDensity(mode))
        End Sub

        Private Shared Sub ApplyTo(app As Application, keys As String(), mode As String)
            For Each key In keys
                Dim variantValue As Object = Nothing
                Dim hasVariantValue = mode <> AppSettingsService.PanelDensityNormal AndAlso
                                 app.TryGetResource(key & "." & mode, Nothing, variantValue)
                Dim baseValue As Object = Nothing
                _base.TryGetValue(key, baseValue)

                If hasVariantValue Then
                    app.Resources(key) = variantValue
                ElseIf baseValue IsNot Nothing Then
                    app.Resources(key) = baseValue
                End If
            Next
        End Sub

        Private Shared Sub EnsureBase(app As Application)
            If _base IsNot Nothing Then Return

            _base = New Dictionary(Of String, Object)(StringComparer.Ordinal)
            For Each key In ToolKeys.Concat(AdjustmentsKeys)
                Dim value As Object = Nothing
                If app.TryGetResource(key, Nothing, value) Then _base(key) = value
            Next
        End Sub

    End Class

End Namespace