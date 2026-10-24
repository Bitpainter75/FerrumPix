Imports System
Imports System.Collections.Generic
Imports System.IO
Imports System.Linq
Imports Avalonia.Controls
Imports Avalonia.Platform
Imports Avalonia.VisualTree
Imports SkiaSharp

Namespace Services

    ''' <summary>Das ICC-Profil je Bildschirm und der Farbfilter, der Fotos dafür umrechnet.
    '''
    ''' <para>WARUM: FerrumPix rechnet in sRGB. Unter Windows und Linux nimmt kein System die Werte
    ''' eines gewöhnlichen Fensters als sRGB und rechnet sie auf den Bildschirm um (Ausnahme: KDE
    ''' Plasma mit gesetztem Farbprofil, Windows mit automatischer Farbverwaltung). Auf einem
    ''' Bildschirm mit weitem Farbumfang sind die Fotos dann übersättigt. Unter macOS erledigt das
    ''' das System, sobald das Fenster einen Farbraum trägt (<see cref="MacWindowColorSpaceService"/>);
    ''' dort gibt es diesen Dienst nicht.</para>
    '''
    ''' <para>WAS: je erkanntem Bildschirm ein vom Nutzer gewähltes ICC-Profil
    ''' (<c>AppSettings.DisplayProfiles</c>, Schlüssel ist der Name des Bildschirms, unter X11 der
    ''' RandR-Name des Anschlusses). Umgerechnet werden nur die Fotos in Betrachter und Editor
    ''' (<see cref="Controls.ColorManagedBitmap"/>); Oberfläche, Miniaturen, Histogramm, Pipette und
    ''' Export bleiben sRGB. Maßgeblich ist der Bildschirm, auf dem das Hauptfenster gerade steht;
    ''' wandert es hinüber, wird nachgezogen.</para>
    '''
    ''' <para>WIE: Skia liest das Profil (<c>SKColorSpace.CreateIcc</c>) und liefert Matrix nach XYZ
    ''' D50 und Tonkurve als Parameterformel; eine Tabellenkurve nähert Skia dabei selbst an. Der
    ''' Filter dekodiert sRGB, rechnet mit der Matrix in den Farbraum des Bildschirms, klemmt auf 0
    ''' bis 1 und kodiert mit der umgekehrten Tonkurve des Bildschirms. Ein Profil ohne Matrix und
    ''' Tonkurve (reine Tabellenprofile) wird mit Grund abgelehnt.</para></summary>
    Public NotInheritable Class DisplayProfileService

        Private Sub New()
        End Sub

        ''' <summary>Unter macOS rechnet das System um; dort bleibt der Dienst aus.</summary>
        Public Shared ReadOnly Property IsSupported As Boolean
            Get
                Return Not OperatingSystem.IsMacOS()
            End Get
        End Property

        ''' <summary>Ausgelöst auf dem Oberflächenfaden, wenn sich der wirksame Filter geändert
        ''' hat (anderer Bildschirm, anderes Profil).</summary>
        Public Shared Event Changed As EventHandler

        ''' <summary>Ein gelesenes Profil: Filter oder Grund, warum es keinen gibt.</summary>
        Public NotInheritable Class LoadedProfile
            Public Property Filter As SKColorFilter
            Public Property Description As String = ""
            Public Property ErrorText As String = ""
            Friend Property Path As String = ""
            Friend Property Stamp As DateTime
        End Class

        Private Shared ReadOnly _cache As New Dictionary(Of String, LoadedProfile)(StringComparer.Ordinal)
        Private Shared ReadOnly _cacheLock As New Object()
        Private Shared _window As Window
        Private Shared _currentKey As String = ""
        Private Shared _current As SKColorFilter

        ''' <summary>Der Filter für den Bildschirm des Hauptfensters, Nothing ohne Profil. Nur auf dem
        ''' Oberflächenfaden lesen; der Zeichenfaden bekommt ihn über die Zeichenoperation mit.
        '''
        ''' Filter werden NIE freigegeben: eine Zeichenoperation kann ihn noch auf dem Zeichenfaden
        ''' benutzen, während hier schon ein anderer gilt. Es sind wenige kleine Objekte.</summary>
        Public Shared ReadOnly Property CurrentFilter As SKColorFilter
            Get
                Return _current
            End Get
        End Property

        ''' <summary>Der Bildschirm, auf dem das Hauptfenster gerade steht (sein Schlüssel).</summary>
        Public Shared ReadOnly Property CurrentScreenKey As String
            Get
                Return _currentKey
            End Get
        End Property

        ''' <summary>Der Schlüssel eines Bildschirms in den Einstellungen.</summary>
        Public Shared Function ScreenKey(screen As Screen) As String
            If screen Is Nothing Then Return ""
            Return If(screen.DisplayName, "").Trim()
        End Function

        ''' <summary>Die erkannten Bildschirme, der Hauptbildschirm zuerst. Leer, solange kein
        ''' Fenster angemeldet ist.</summary>
        Public Shared Function DetectedScreens() As IReadOnlyList(Of Screen)
            Dim screens = _window?.Screens
            If screens Is Nothing Then Return Array.Empty(Of Screen)()
            Return screens.All.
                Where(Function(s) ScreenKey(s).Length > 0).
                OrderByDescending(Function(s) s.IsPrimary).
                ThenBy(Function(s) s.Bounds.X).
                ToList()
        End Function

        ''' <summary>Meldet das Hauptfenster an: ab jetzt folgt der Filter dem Bildschirm, auf dem es
        ''' steht.</summary>
        Public Shared Sub Attach(window As Window)
            If window Is Nothing OrElse Not IsSupported Then Return
            _window = window
            AddHandler window.PositionChanged, Sub(sender, e) Refresh()
            AddHandler window.ScalingChanged, Sub(sender, e) Refresh()
            Dim screens = window.Screens
            ' Ein Bildschirm kommt dazu oder geht: auch ohne neuen Filter muss die Liste in den
            ' Einstellungen nachziehen.
            If screens IsNot Nothing Then AddHandler screens.Changed, Sub(sender, e) Refresh(announce:=True)
            Refresh()
        End Sub

        ''' <summary>Rechnet nach, welcher Filter gilt; bei einer Änderung werden die Fotos neu
        ''' gezeichnet. Auch nach dem Ändern eines Profils in den Einstellungen aufzurufen.
        ''' <paramref name="announce"/> meldet <see cref="Changed"/> auch ohne Änderung.</summary>
        Public Shared Sub Refresh(Optional announce As Boolean = False)
            If _window Is Nothing Then Return
            Dim screen = _window.Screens?.ScreenFromWindow(_window)
            Dim key = ScreenKey(screen)
            Dim path As String = Nothing
            Dim profiles = AppSettingsService.Load().DisplayProfiles
            If profiles IsNot Nothing AndAlso key.Length > 0 Then profiles.TryGetValue(key, path)
            Dim filter = If(String.IsNullOrWhiteSpace(path), Nothing, Load(path).Filter)
            If String.Equals(key, _currentKey, StringComparison.Ordinal) AndAlso ReferenceEquals(filter, _current) Then
                If announce Then RaiseEvent Changed(Nothing, EventArgs.Empty)
                Return
            End If
            _currentKey = key
            _current = filter
            DiagnosticLogService.LogAlways("DisplayProfile",
                $"screen={key} profile={If(String.IsNullOrWhiteSpace(path), "-", IO.Path.GetFileName(path))} active={filter IsNot Nothing}")
            InvalidatePhotos()
            RaiseEvent Changed(Nothing, EventArgs.Empty)
        End Sub

        ''' <summary>Zeichnet alle Fotos neu, die über <see cref="Controls.ColorManagedBitmap"/>
        ''' laufen. Eine Bildquelle kann sich nicht selbst neu zeichnen lassen, das Bild-Element schon.</summary>
        Private Shared Sub InvalidatePhotos()
            If _window Is Nothing Then Return
            For Each image In _window.GetVisualDescendants().OfType(Of Image)()
                If TypeOf image.Source Is Controls.ColorManagedBitmap Then image.InvalidateVisual()
            Next
        End Sub

        ''' <summary>Liest ein Profil, zwischengespeichert nach Pfad und Änderungszeit: ein neu
        ''' kalibriertes Profil unter demselben Namen wird beim nächsten Nachrechnen gelesen.</summary>
        Public Shared Function Load(path As String) As LoadedProfile
            Dim stamp As DateTime = DateTime.MinValue
            Try
                If Not File.Exists(path) Then
                    Return New LoadedProfile With {.Path = path, .ErrorText = LocalizationService.T("Die Profildatei fehlt.")}
                End If
                stamp = File.GetLastWriteTimeUtc(path)
            Catch ex As Exception
                Return New LoadedProfile With {.Path = path, .ErrorText = LocalizationService.T("Die Profildatei fehlt.")}
            End Try

            SyncLock _cacheLock
                Dim cached As LoadedProfile = Nothing
                If _cache.TryGetValue(path, cached) AndAlso cached.Stamp = stamp Then Return cached
            End SyncLock

            Dim loaded As LoadedProfile
            Try
                loaded = FromColorSpace(SKColorSpace.CreateIcc(File.ReadAllBytes(path)))
            Catch ex As Exception
                DiagnosticLogService.LogException("DisplayProfile.Load", ex)
                loaded = New LoadedProfile With {.ErrorText = LocalizationService.T("Die Datei ist kein lesbares ICC-Profil.")}
            End Try
            loaded.Path = path
            loaded.Stamp = stamp
            SyncLock _cacheLock
                _cache(path) = loaded
            End SyncLock
            Return loaded
        End Function

        ''' <summary>Filter aus einem Farbraum. Getrennt vom Lesen der Datei, damit der Prüfstand
        ''' ihn ohne Datei mit einem bekannten Farbraum füttern kann.</summary>
        Friend Shared Function FromColorSpace(display As SKColorSpace) As LoadedProfile
            If display Is Nothing Then
                Return New LoadedProfile With {.ErrorText = LocalizationService.T("Die Datei ist kein lesbares ICC-Profil.")}
            End If
            Dim displayFn As SKColorSpaceTransferFn
            Dim displayXyz As SKColorSpaceXyz
            If Not display.GetNumericalTransferFunction(displayFn) OrElse Not display.ToColorSpaceXyz(displayXyz) Then
                Return New LoadedProfile With {.ErrorText = LocalizationService.T("Das Profil hat keine Matrix mit Tonkurve und lässt sich nicht verwenden.")}
            End If

            ' sRGB-linear nach Bildschirm-linear: erst nach XYZ D50, dann zurück mit der Umkehrung
            ' der Bildschirm-Matrix. Beide Matrizen stehen im ICC schon auf D50 angepasst.
            Dim srgb = SKColorSpaceXyz.Srgb
            Dim toDisplay = SKColorSpaceXyz.Concat(displayXyz.Invert(), srgb)
            Dim inverse = displayFn.Invert()
            If Not IsFinite(toDisplay.Values) OrElse Not IsFinite(inverse.Values) Then
                Return New LoadedProfile With {.ErrorText = LocalizationService.T("Das Profil hat keine Matrix mit Tonkurve und lässt sich nicht verwenden.")}
            End If

            Dim effect = GetEffect()
            If effect Is Nothing Then Return New LoadedProfile With {.ErrorText = "SkSL"}
            Dim u = New SKRuntimeEffectUniforms(effect)
            For row As Integer = 0 To 2
                u("m" & row.ToString(Globalization.CultureInfo.InvariantCulture)) =
                    New Single() {toDisplay(0, row), toDisplay(1, row), toDisplay(2, row)}
            Next
            Dim s = SKColorSpaceTransferFn.Srgb
            u("s0") = New Single() {s.G, s.A, s.B, s.C}
            u("s1") = New Single() {s.D, s.E, s.F}
            u("d0") = New Single() {inverse.G, inverse.A, inverse.B, inverse.C}
            u("d1") = New Single() {inverse.D, inverse.E, inverse.F}

            ' Der Rotpunkt sRGB auf dem Bildschirm, als Hinweis in den Einstellungen und im Protokoll.
            Dim description = String.Format(Globalization.CultureInfo.InvariantCulture,
                "gamma {0:0.00}, red {1:0.000}/{2:0.000}/{3:0.000}", displayFn.G, toDisplay(0, 0), toDisplay(0, 1), toDisplay(0, 2))
            Return New LoadedProfile With {.Filter = effect.ToColorFilter(u), .Description = description}
        End Function

        Private Shared Function IsFinite(values As Single()) As Boolean
            Return values IsNot Nothing AndAlso values.All(Function(v) Not Single.IsNaN(v) AndAlso Not Single.IsInfinity(v))
        End Function

        Private Shared _effect As SKRuntimeEffect
        Private Shared ReadOnly _effectLock As New Object()

        ''' <summary>Der Farbfilter als SkSL. Die Eingabe ist vormultipliziert; gerechnet wird auf
        ''' geraden Farben, sonst verschöbe eine halb durchsichtige Kante ihren Farbton.
        ''' Tonkurve in der Parameterform von Skia: x &lt; d ? c·x + f : (a·x + b)^g + e.</summary>
        Private Shared Function GetEffect() As SKRuntimeEffect
            SyncLock _effectLock
                If _effect IsNot Nothing Then Return _effect
                Const Source As String =
                    "uniform float3 m0; uniform float3 m1; uniform float3 m2;" & vbLf &
                    "uniform float4 s0; uniform float3 s1;" & vbLf &
                    "uniform float4 d0; uniform float3 d1;" & vbLf &
                    "float tf(float x, float4 p, float3 q) {" & vbLf &
                    "  float v = max(x, 0.0);" & vbLf &
                    "  return v < q.x ? p.w * v + q.z : pow(p.y * v + p.z, p.x) + q.y;" & vbLf &
                    "}" & vbLf &
                    "half4 main(half4 c) {" & vbLf &
                    "  float a = c.a;" & vbLf &
                    "  if (a <= 0.0) return c;" & vbLf &
                    "  float3 v = float3(c.rgb) / a;" & vbLf &
                    "  v = float3(tf(v.r, s0, s1), tf(v.g, s0, s1), tf(v.b, s0, s1));" & vbLf &
                    "  v = clamp(float3(dot(m0, v), dot(m1, v), dot(m2, v)), 0.0, 1.0);" & vbLf &
                    "  v = float3(tf(v.r, d0, d1), tf(v.g, d0, d1), tf(v.b, d0, d1));" & vbLf &
                    "  return half4(half3(v * a), c.a);" & vbLf &
                    "}"
                Dim errors As String = Nothing
                _effect = SKRuntimeEffect.CreateColorFilter(Source, errors)
                If _effect Is Nothing Then DiagnosticLogService.LogAlways("DisplayProfile", "SkSL: " & errors)
                Return _effect
            End SyncLock
        End Function

    End Class

End Namespace
