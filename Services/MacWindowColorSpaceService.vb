Imports System
Imports System.Reflection
Imports System.Runtime.InteropServices
Imports System.Text

Namespace Services

    ''' <summary>Setzt unter macOS den FARBRAUM der Zeichenflaeche des Fensters auf sRGB.
    '''
    ''' Das Problem, das dahintersteht (Nutzerbericht 2026-09-05, sauber gemessen an zwei
    ''' kalibrierten Bildschirmen desselben Modells): FerrumPix rechnet intern in sRGB und gibt
    ''' diese Zahlen an das Toolkit weiter. Traegt die Zeichenflaeche keinen Farbraum, nimmt macOS
    ''' die Zahlen als Werte DES BILDSCHIRMS. Auf einem Bildschirm mit weitem Farbumfang wird damit
    ''' alles zu satt - nicht nur die Fotos, sondern jedes Element der Oberflaeche. Gleich bleiben
    ''' allein die Fensterknoepfe, die das System selbst zeichnet.
    '''
    ''' Traegt die Flaeche dagegen sRGB, rechnet macOS selbst um, fuer jeden Bildschirm richtig und
    ''' auch dann, wenn das Fenster zwischen zwei verschiedenen hin und her wandert. Deshalb setzt
    ''' dieser Dienst einen Farbraum, statt Farben umzurechnen: eine eigene Umrechnung erwischt die
    ''' Fotos und muesste die ganze Oberflaeche einzeln nachziehen.
    '''
    ''' <para>MEHRERE VERFAHREN ZUR WAHL, und der Grund ist ein Messbefund. Der erste Versuch legte
    ''' sRGB auf die Ebene der Ansicht; der Melder bekam am 2026-09-06 zurueck, dass diese Ebene
    ''' eine <c>NSViewBackingLayer</c> ist und <c>setColorspace:</c> gar nicht kennt. Damit ist
    ''' EINE Frage beantwortet und die naechste offen: welche Ebene traegt den Farbraum, und traegt
    ''' ueberhaupt eine? In <c>libAvaloniaNative.dylib</c> nachgesehen: nur der Metal-Weg legt eine
    ''' <c>CAMetalLayer</c> an, der OpenGL- und der Software-Weg zeichnen in eine IOSurface und
    ''' haengen sie als Inhalt der gewoehnlichen Backing-Ebene ein. Ein Farbraum ist dort auf der
    ''' Ebene nicht unterzubringen, wohl aber vielleicht am FENSTER (<c>NSWindow.colorSpace</c>).
    ''' Statt jede Vermutung einzeln auszuliefern, stehen alle Verfahren, die noch in Betracht
    ''' kommen, zur Wahl - wer ein Geraet mit weitem Farbumfang hat, probiert sie durch und liest
    ''' unter der Auswahl ab, was passiert ist.</para>
    '''
    ''' <para>Ausserhalb von macOS tut hier nichts etwas: <see cref="Apply"/> kehrt sofort um, und
    ''' keine der Deklarationen unten wird je angefasst. Das heisst NICHT, dass es das Problem dort
    ''' nicht gaebe - eine sRGB-Ausgabe wird auf jedem Bildschirm mit weitem Farbumfang zu satt.
    ''' Verschieden ist die Zustaendigkeit: macOS rechnet selbst um, sobald die Flaeche einen
    ''' Farbraum traegt. Windows tut das fuer gewoehnliche Fenster nicht (erst Windows 11 mit
    ''' eingeschalteter automatischer Farbverwaltung), und unter X11 gibt es nichts dergleichen.
    ''' Dort waere eine eigene Umrechnung auf das Bildschirmprofil noetig, nicht ein Kennzeichen.</para></summary>
    Public NotInheritable Class MacWindowColorSpaceService

        Private Sub New()
        End Sub

        Private Const ObjCRuntime As String = "/usr/lib/libobjc.A.dylib"
        Private Const CoreGraphicsFramework As String =
            "/System/Library/Frameworks/CoreGraphics.framework/CoreGraphics"

        ' ── Die Verfahren ────────────────────────────────────────────────────────
        '
        ' Die Zeichenketten liegen in der settings.json und bleiben deshalb, wie sie sind. Sie sind
        ' KEINE Anzeigetexte; die Beschriftung der Auswahl steht im SettingsViewModel.

        ''' <summary>Gar nichts tun. Vorgabe.</summary>
        Public Const MethodOff As String = "Off"

        ''' <summary>Die Ebene der Ansicht, mit <c>setWantsLayer:</c> davor. Das ist der Weg aus
        ''' 0.9.38, an dem der Melder eine <c>NSViewBackingLayer</c> vorfand.</summary>
        Public Const MethodViewLayer As String = "ViewLayer"

        ''' <summary>Dieselbe Ebene, aber OHNE <c>setWantsLayer:</c>. Das Toolkit ruft es nirgends
        ''' selbst (in der Bibliothek nachgesehen), also kann unser Aufruf derjenige gewesen sein,
        ''' der die leere Backing-Ebene ueberhaupt erst hat entstehen lassen.</summary>
        Public Const MethodViewLayerNoWantsLayer As String = "ViewLayerNoWantsLayer"

        ''' <summary>Den Ebenen- und Ansichtsbaum durchsuchen und die erste Ebene nehmen, die
        ''' <c>setColorspace:</c> kennt. Trifft den Fall, dass die Metal-Ebene tiefer haengt.</summary>
        Public Const MethodLayerTree As String = "LayerTree"

        ''' <summary>Die Ebene der Inhaltsansicht des Fensters. Sie muss nicht dieselbe sein wie
        ''' die Ansicht, die das Toolkit als Handle herausgibt.</summary>
        Public Const MethodContentViewLayer As String = "ContentViewLayer"

        ''' <summary>Der Farbraum des FENSTERS (<c>NSWindow.colorSpace</c>). Andere Ebene der
        ''' Betrachtung: nicht die Ebene, sondern der Zeichenspeicher des Fensters.</summary>
        Public Const MethodWindow As String = "Window"

        ''' <summary>Fenster und Ebene zusammen. Falls beides zusammenwirken muss.</summary>
        Public Const MethodWindowAndLayer As String = "WindowAndLayer"

        ''' <summary>Alle Verfahren in der Reihenfolge der Auswahlliste.</summary>
        Public Shared ReadOnly Methods As String() = {
            MethodOff, MethodViewLayer, MethodViewLayerNoWantsLayer, MethodLayerTree,
            MethodContentViewLayer, MethodWindow, MethodWindowAndLayer}

        ''' <summary>Unbekanntes faellt auf "nichts tun" zurueck - eine Einstellung aus einer
        ''' neueren Fassung darf hier kein Verfahren erfinden.</summary>
        Public Shared Function NormalizeMethod(value As String) As String
            If String.IsNullOrWhiteSpace(value) Then Return MethodOff
            Dim trimmed = value.Trim()
            For Each candidate In Methods
                If String.Equals(candidate, trimmed, StringComparison.OrdinalIgnoreCase) Then Return candidate
            Next
            Return MethodOff
        End Function

        ''' <summary>Was der letzte Versuch ergeben hat - fuer die Anzeige in den Einstellungen und
        ''' fuer Rueckfragen an jemanden, der es ausprobiert.</summary>
        Public Shared Property LastResult As String = ""

        ''' <summary>Was VORGEFUNDEN wurde, unabhaengig vom Verfahren: Grafikweg, Klasse der
        ''' Ansicht, Klasse der Ebene, Unterebenen, Ebenen der Unteransichten. Getrennt von
        ''' <see cref="LastResult"/>, damit es auch bei "aus" etwas zu berichten gibt.</summary>
        Public Shared Property LastEnvironment As String = ""

        ''' <summary>Das Verfahren wurde in den Einstellungen gewechselt. Das Hauptfenster haengt
        ''' daran und versucht es sofort neu - fuer die meisten Verfahren braucht es dazu keinen
        ''' Neustart, und wer eine Reihe davon durchprobiert, will nicht sieben Mal starten.</summary>
        Public Shared Event MethodChanged As EventHandler

        ''' <summary>Loest <see cref="MethodChanged"/> aus. RaiseEvent geht nur in der Klasse
        ''' selbst, deshalb dieser Weg fuer das ViewModel.</summary>
        Public Shared Sub NotifyMethodChanged()
            RaiseEvent MethodChanged(Nothing, EventArgs.Empty)
        End Sub

        ''' <summary>Der Grafikweg, den Avalonia tatsaechlich gewaehlt hat.
        '''
        ''' Der wichtigste Wert im ganzen Bericht: nur der Metal-Weg legt eine Ebene an, die einen
        ''' Farbraum tragen KANN. Steht hier OpenGL oder Software, ist jede Suche nach einer
        ''' <c>CAMetalLayer</c> vergebens, und die Antwort liegt am Fenster oder in einer eigenen
        ''' Umrechnung.</summary>
        Public Shared ReadOnly Property GraphicsPathName As String
            Get
                Try
                    ' UEBER REFLEXION, und der Grund steht hier: Avalonia 12.1.2 liefert zwei
                    ' Assemblys, und in der, gegen die uebersetzt wird, ist AvaloniaLocator.Current
                    ' als Friend markiert (PrivateApi). Ein direkter Zugriff uebersetzt deshalb
                    ' nicht, obwohl er zur Laufzeit da ist. Faellt der Weg weg, steht hier
                    ' "unbekannt" - eine Diagnosezeile darf nichts kosten.
                    Dim locatorType = Type.GetType("Avalonia.AvaloniaLocator, Avalonia.Base")
                    Dim graphicsInterface = Type.GetType("Avalonia.Platform.IPlatformGraphics, Avalonia.Base")
                    If locatorType Is Nothing OrElse graphicsInterface Is Nothing Then Return "unbekannt (Avalonia.Base)"
                    Dim resolver = locatorType.GetProperty(
                        "Current", BindingFlags.Public Or BindingFlags.NonPublic Or BindingFlags.Static)?.GetValue(Nothing)
                    If resolver Is Nothing Then Return "unbekannt (kein Resolver)"
                    Dim getService = resolver.GetType().GetMethod("GetService", New Type() {GetType(Type)})
                    Dim graphics = getService?.Invoke(resolver, New Object() {graphicsInterface})
                    If graphics Is Nothing Then Return "Software (kein IPlatformGraphics)"
                    Dim name = graphics.GetType().Name
                    If name.Contains("Metal", StringComparison.OrdinalIgnoreCase) Then Return "Metal (" & name & ")"
                    If name.Contains("Gl", StringComparison.Ordinal) Then Return "OpenGL (" & name & ")"
                    Return name
                Catch ex As Exception
                    Return "unbekannt (" & ex.GetType().Name & ")"
                End Try
            End Get
        End Property

        ''' <summary>Setzt den Farbraum nach dem gewaehlten Verfahren.</summary>
        ''' <param name="windowHandle">Das Handle aus <c>TopLevel.TryGetPlatformHandle</c>.</param>
        ''' <param name="descriptor">Dessen <c>HandleDescriptor</c>. Erwartet wird NSView oder
        ''' NSWindow; bei einem Fenster wird zuerst dessen Inhaltsansicht geholt.</param>
        ''' <param name="method">Eines der <see cref="Methods"/>.</param>
        ''' <returns>True, wenn der Farbraum tatsaechlich gesetzt und nachgelesen wurde. Bei "aus"
        ''' immer True: es gibt nichts zu wiederholen.</returns>
        Public Shared Function Apply(windowHandle As IntPtr, descriptor As String, method As String) As Boolean
            If Not OperatingSystem.IsMacOS() Then Return False
            Dim chosen = NormalizeMethod(method)
            If windowHandle = IntPtr.Zero Then
                Report("kein Fenster-Handle")
                Return False
            End If

            Try
                ' Ansicht UND Fenster besorgen, unabhaengig davon, was das Handle beschreibt: die
                ' Verfahren brauchen beide Enden.
                Dim view = windowHandle
                Dim window = IntPtr.Zero
                If String.Equals(descriptor, "NSWindow", StringComparison.OrdinalIgnoreCase) Then
                    window = windowHandle
                    view = MsgSend(window, Selector("contentView"))
                Else
                    window = MsgSend(view, Selector("window"))
                End If

                LastEnvironment = DescribeEnvironment(view, window)

                If chosen = MethodOff Then
                    Report("aus")
                    Return True
                End If

                Select Case chosen
                    Case MethodViewLayer
                        Return TagLayerOfView(view, forceLayer:=True)
                    Case MethodViewLayerNoWantsLayer
                        Return TagLayerOfView(view, forceLayer:=False)
                    Case MethodContentViewLayer
                        If window = IntPtr.Zero Then
                            Report("kein Fenster zur Ansicht")
                            Return False
                        End If
                        Dim contentView = MsgSend(window, Selector("contentView"))
                        If contentView = IntPtr.Zero Then
                            Report("Fenster ohne contentView")
                            Return False
                        End If
                        Return TagLayerOfView(contentView, forceLayer:=False)
                    Case MethodLayerTree
                        Return TagFirstLayerInTree(view)
                    Case MethodWindow
                        Return TagWindow(window)
                    Case MethodWindowAndLayer
                        ' BEIDE Ausgaenge werden gemeldet. Fertig ist der Versuch erst, wenn BEIDE
                        ' sitzen: der Rueckgabewert bremst den Wiederholer im Hauptfenster, und der
                        ' soll weiter nach der Ebene sehen, auch wenn das Fenster schon getragen hat.
                        Dim windowResult = TagWindow(window)
                        Dim windowText = LastResult
                        Dim layerResult = TagFirstLayerInTree(view)
                        Report("Fenster: " & windowText & " / Ebene: " & LastResult)
                        Return windowResult AndAlso layerResult
                    Case Else
                        Report("unbekanntes Verfahren")
                        Return False
                End Select
            Catch ex As Exception
                Report("fehlgeschlagen: " & ex.Message)
                DiagnosticLogService.LogException("Farbraum.Fenster", ex)
                Return False
            End Try
        End Function

        ''' <summary>Legt sRGB auf die Ebene DIESER Ansicht.</summary>
        ''' <param name="forceLayer">Vorher <c>setWantsLayer:</c> rufen. Erzwingt eine Ebene, wo
        ''' keine ist - kann aber genau die leere Backing-Ebene sein, die dann gemeldet wird.</param>
        Private Shared Function TagLayerOfView(view As IntPtr, forceLayer As Boolean) As Boolean
            If view = IntPtr.Zero Then
                Report("keine Ansicht")
                Return False
            End If
            If forceLayer AndAlso RespondsTo(view, "setWantsLayer:") Then
                MsgSendBoolArg(view, Selector("setWantsLayer:"), True)
            End If

            Dim layer = MsgSend(view, Selector("layer"))
            If layer = IntPtr.Zero Then
                Report("die Ansicht (" & ClassNameOf(view) & ") hat keine Ebene")
                Return False
            End If

            ' DIE entscheidende Stelle: eine CAMetalLayer kennt setColorspace:, eine schlichte
            ' CALayer nicht. Gefragt wird das Objekt selbst, statt seinen Typ zu raten.
            If Not RespondsTo(layer, "setColorspace:") Then
                ' MIT DEM NAMEN DER KLASSE. "Kennt es nicht" allein liess offen, ob dort die
                ' Metall-Ebene des Toolkits sitzt oder eine leere, die AppKit auf unser
                ' wantsLayer hin angelegt hat und die gleich darauf ersetzt wird.
                Report($"die Ebene ({ClassNameOf(layer)}) kennt setColorspace: nicht - " &
                       "entweder ist sie noch nicht die des Zeichenwegs, oder dieser Weg traegt hier nicht")
                Return False
            End If
            Return TagLayer(layer)
        End Function

        ''' <summary>Sucht im Baum aus Ebenen und Unteransichten die erste Ebene, die einen
        ''' Farbraum annimmt, und legt sRGB darauf.</summary>
        Private Shared Function TagFirstLayerInTree(view As IntPtr) As Boolean
            If view = IntPtr.Zero Then
                Report("keine Ansicht")
                Return False
            End If
            Dim found = FindTaggableLayer(view, 0)
            If found = IntPtr.Zero Then
                Report("im Baum unter " & ClassNameOf(view) & " kennt keine Ebene setColorspace:")
                Return False
            End If
            Return TagLayer(found)
        End Function

        ''' <summary>Die erste Ebene im Baum, die <c>setColorspace:</c> kennt. Geht die Ebene
        ''' dieser Ansicht samt Unterebenen durch und danach die Unteransichten.
        '''
        ''' OHNE <c>setWantsLayer:</c>: das Suchen darf nicht selbst Ebenen erzeugen, sonst
        ''' berichtet es ueber einen Zustand, den es gerade hergestellt hat.</summary>
        Private Shared Function FindTaggableLayer(view As IntPtr, depth As Integer) As IntPtr
            If view = IntPtr.Zero OrElse depth > MaxTreeDepth Then Return IntPtr.Zero

            Dim layer = MsgSend(view, Selector("layer"))
            Dim inLayer = FindTaggableSublayer(layer, 0)
            If inLayer <> IntPtr.Zero Then Return inLayer

            Dim subviews = MsgSend(view, Selector("subviews"))
            For index = 0 To ArrayCount(subviews) - 1
                Dim child = ArrayItem(subviews, index)
                Dim inChild = FindTaggableLayer(child, depth + 1)
                If inChild <> IntPtr.Zero Then Return inChild
            Next
            Return IntPtr.Zero
        End Function

        Private Shared Function FindTaggableSublayer(layer As IntPtr, depth As Integer) As IntPtr
            If layer = IntPtr.Zero OrElse depth > MaxTreeDepth Then Return IntPtr.Zero
            If RespondsTo(layer, "setColorspace:") Then Return layer
            Dim sublayers = MsgSend(layer, Selector("sublayers"))
            For index = 0 To ArrayCount(sublayers) - 1
                Dim found = FindTaggableSublayer(ArrayItem(sublayers, index), depth + 1)
                If found <> IntPtr.Zero Then Return found
            Next
            Return IntPtr.Zero
        End Function

        ''' <summary>Sechs Ebenen tief ist mehr als jeder erwartete Baum und begrenzt zugleich den
        ''' Schaden, wenn eine Ebene sich selbst enthaelt.</summary>
        Private Const MaxTreeDepth As Integer = 6

        ''' <summary>Legt sRGB auf eine Ebene, die <c>setColorspace:</c> kennt, und liest nach.</summary>
        Private Shared Function TagLayer(layer As IntPtr) As Boolean
            Dim srgb = CreateSrgbColorSpace()
            If srgb = IntPtr.Zero Then
                Report("sRGB-Farbraum liess sich nicht anlegen")
                Return False
            End If

            Dim layerClass = ClassNameOf(layer)
            Try
                MsgSendPtrArg(layer, Selector("setColorspace:"), srgb)

                ' NACHGELESEN, NICHT ANGENOMMEN. Ein setColorspace:, das die Ebene annimmt,
                ' beantwortet ihr colorspace mit demselben Farbraum. Bleibt es leer oder steht
                ' etwas anderes darin, war der Aufruf folgenlos - und genau das ist der Fall,
                ' den ein Nutzer sonst als "wirkt nicht" meldet, ohne dass wir wissen, woran es
                ' lag (Bericht 2026-09-06).
                Dim current = MsgSend(layer, Selector("colorspace"))
                If current = IntPtr.Zero Then
                    Report($"gesetzt, aber die Ebene ({layerClass}) meldet weiterhin keinen Farbraum")
                    Return False
                End If
                If Not CGColorSpaceEqualToColorSpace(current, srgb) Then
                    Report($"gesetzt, aber die Ebene ({layerClass}) meldet einen anderen Farbraum")
                    Return False
                End If
            Finally
                ' Die Ebene haelt den Farbraum selbst fest; unsere Zaehlung geht zurueck.
                CGColorSpaceRelease(srgb)
            End Try

            Report($"sRGB sitzt auf der Ebene ({layerClass}), nachgelesen")
            Return True
        End Function

        ''' <summary>Legt sRGB auf das FENSTER.
        '''
        ''' <c>NSWindow.colorSpace</c> gilt fuer den Zeichenspeicher des Fensters, nicht fuer eine
        ''' Ebene, und nimmt eine <c>NSColorSpace</c> statt einer <c>CGColorSpace</c>. Ob der Weg
        ''' auch dann greift, wenn der Inhalt als IOSurface an einer Ebene haengt, weiss hier
        ''' niemand - deshalb steht er zur Wahl und wird nachgelesen.</summary>
        Private Shared Function TagWindow(window As IntPtr) As Boolean
            If window = IntPtr.Zero Then
                Report("kein Fenster")
                Return False
            End If
            If Not RespondsTo(window, "setColorSpace:") Then
                Report($"das Fenster ({ClassNameOf(window)}) kennt setColorSpace: nicht")
                Return False
            End If

            Dim colorSpaceClass = objc_getClass("NSColorSpace")
            If colorSpaceClass = IntPtr.Zero Then
                Report("NSColorSpace nicht gefunden")
                Return False
            End If
            Dim srgb = MsgSend(colorSpaceClass, Selector("sRGBColorSpace"))
            If srgb = IntPtr.Zero Then
                Report("NSColorSpace sRGBColorSpace liefert nichts")
                Return False
            End If

            MsgSendPtrArg(window, Selector("setColorSpace:"), srgb)

            ' Auch hier nachgelesen. Der Vergleich geht ueber isEqual:, weil sRGBColorSpace zwar
            ' ein gemeinsames Objekt zurueckgibt, das Fenster aber eine eigene Kopie halten darf.
            Dim current = MsgSend(window, Selector("colorSpace"))
            If current = IntPtr.Zero Then
                Report($"gesetzt, aber das Fenster ({ClassNameOf(window)}) meldet keinen Farbraum")
                Return False
            End If
            If current <> srgb AndAlso Not MsgSendBoolReturn(current, Selector("isEqual:"), srgb) Then
                Report("gesetzt, aber das Fenster meldet " & NameOfColorSpace(current))
                Return False
            End If

            Report("sRGB sitzt am Fenster (" & ClassNameOf(window) & "), nachgelesen")
            Return True
        End Function

        ''' <summary>Was rund um Ansicht und Fenster vorliegt. Diese Zeile ist der eigentliche
        ''' Bericht: sie sagt, welcher Grafikweg laeuft und welche Ebenen es ueberhaupt gibt.</summary>
        Private Shared Function DescribeEnvironment(view As IntPtr, window As IntPtr) As String
            Dim text As New StringBuilder()
            Try
                text.Append("Grafikweg: ").Append(GraphicsPathName)
                text.Append("; Fenster: ").Append(ClassNameOf(window))
                If window <> IntPtr.Zero AndAlso RespondsTo(window, "colorSpace") Then
                    text.Append(" (Farbraum ").Append(NameOfColorSpace(MsgSend(window, Selector("colorSpace")))).Append(")")
                End If
                text.Append("; Ansicht: ").Append(ClassNameOf(view))
                If view <> IntPtr.Zero AndAlso RespondsTo(view, "wantsLayer") Then
                    text.Append(If(MsgSendBoolReturnNoArg(view, Selector("wantsLayer")), " (wantsLayer an)", " (wantsLayer aus)"))
                End If
                text.Append("; Ebenen: ").Append(DescribeLayerTree(view, 0))
            Catch ex As Exception
                text.Append(" [Bericht abgebrochen: ").Append(ex.Message).Append("]")
            End Try
            Return text.ToString()
        End Function

        ''' <summary>Der Baum als Text, mit Klassennamen und einem Vermerk an jeder Ebene, die
        ''' einen Farbraum annehmen wuerde. Ohne <c>setWantsLayer:</c>, siehe
        ''' <see cref="FindTaggableLayer"/>.</summary>
        Private Shared Function DescribeLayerTree(view As IntPtr, depth As Integer) As String
            If view = IntPtr.Zero Then Return "keine"
            If depth > 3 Then Return "..."

            Dim text As New StringBuilder()
            Dim layer = MsgSend(view, Selector("layer"))
            text.Append(ClassNameOf(view)).Append("/").Append(ClassNameOf(layer))
            If layer <> IntPtr.Zero AndAlso RespondsTo(layer, "setColorspace:") Then text.Append(" [nimmt Farbraum]")

            Dim sublayers = MsgSend(layer, Selector("sublayers"))
            Dim sublayerCount = ArrayCount(sublayers)
            If sublayerCount > 0 Then
                text.Append(" Unterebenen(")
                For index = 0 To Math.Min(sublayerCount, MaxListed) - 1
                    If index > 0 Then text.Append(", ")
                    Dim sublayer = ArrayItem(sublayers, index)
                    text.Append(ClassNameOf(sublayer))
                    If RespondsTo(sublayer, "setColorspace:") Then text.Append(" [nimmt Farbraum]")
                Next
                If sublayerCount > MaxListed Then text.Append(", +").Append(sublayerCount - MaxListed)
                text.Append(")")
            End If

            Dim subviews = MsgSend(view, Selector("subviews"))
            Dim subviewCount = ArrayCount(subviews)
            If subviewCount > 0 Then
                text.Append(" Unteransichten(")
                For index = 0 To Math.Min(subviewCount, MaxListed) - 1
                    If index > 0 Then text.Append(", ")
                    text.Append(DescribeLayerTree(ArrayItem(subviews, index), depth + 1))
                Next
                If subviewCount > MaxListed Then text.Append(", +").Append(subviewCount - MaxListed)
                text.Append(")")
            End If
            Return text.ToString()
        End Function

        ''' <summary>Mehr als vier Geschwister sagen nichts mehr und machen die Zeile unlesbar.</summary>
        Private Const MaxListed As Integer = 4

        ''' <summary>Nur bei einer AENDERUNG ins Protokoll. Der Versuch wird wiederholt, bis die
        ''' Ebene des Zeichenwegs steht; ohne diese Bremse stuenden dieselben Zeilen dutzendfach da
        ''' und die eine, auf die es ankommt, ginge darin unter.</summary>
        Private Shared _lastLogged As String = ""

        Private Shared Sub Report(text As String)
            LastResult = text
            If String.Equals(_lastLogged, text, StringComparison.Ordinal) Then Return
            _lastLogged = text
            DiagnosticLogService.LogAlways("Farbraum", "Fensterfarbraum: " & text & " | " & LastEnvironment)
        End Sub

        ''' <summary>Der sRGB-Farbraum von CoreGraphics. Sein Name ist eine exportierte Konstante,
        ''' kein Zeichenkettenliteral - sie wird deshalb aus der Bibliothek gelesen.</summary>
        Private Shared Function CreateSrgbColorSpace() As IntPtr
            Dim library As IntPtr
            If Not NativeLibrary.TryLoad(CoreGraphicsFramework, library) Then Return IntPtr.Zero
            Dim exported As IntPtr
            If Not NativeLibrary.TryGetExport(library, "kCGColorSpaceSRGB", exported) Then Return IntPtr.Zero
            Dim name = Marshal.ReadIntPtr(exported)
            If name = IntPtr.Zero Then Return IntPtr.Zero
            Return CGColorSpaceCreateWithName(name)
        End Function

        ''' <summary>Der Klassenname eines ObjC-Objekts, fuer die Meldung. Leer, wenn es keinen gibt.</summary>
        Private Shared Function ClassNameOf(target As IntPtr) As String
            If target = IntPtr.Zero Then Return "keine"
            Try
                Dim name = object_getClassName(target)
                If name = IntPtr.Zero Then Return "unbekannt"
                Return If(Marshal.PtrToStringAnsi(name), "unbekannt")
            Catch
                Return "unbekannt"
            End Try
        End Function

        ''' <summary>Der lesbare Name einer <c>NSColorSpace</c>, fuer den Bericht.</summary>
        Private Shared Function NameOfColorSpace(colorSpace As IntPtr) As String
            If colorSpace = IntPtr.Zero Then Return "keiner"
            Try
                If Not RespondsTo(colorSpace, "localizedName") Then Return ClassNameOf(colorSpace)
                Dim name = MsgSend(colorSpace, Selector("localizedName"))
                If name = IntPtr.Zero Then Return ClassNameOf(colorSpace)
                Dim utf8 = MsgSend(name, Selector("UTF8String"))
                If utf8 = IntPtr.Zero Then Return ClassNameOf(colorSpace)
                Return If(Marshal.PtrToStringUTF8(utf8), ClassNameOf(colorSpace))
            Catch
                Return "unbekannt"
            End Try
        End Function

        ''' <summary>Laenge einer NSArray. Null, wenn dort keine steht.</summary>
        Private Shared Function ArrayCount(array As IntPtr) As Integer
            If array = IntPtr.Zero Then Return 0
            If Not RespondsTo(array, "count") Then Return 0
            Dim count = MsgSendUIntReturn(array, Selector("count")).ToInt64()
            If count <= 0 Then Return 0
            Return CInt(Math.Min(count, 64))
        End Function

        Private Shared Function ArrayItem(array As IntPtr, index As Integer) As IntPtr
            If array = IntPtr.Zero Then Return IntPtr.Zero
            Return MsgSendUIntArg(array, Selector("objectAtIndex:"), New IntPtr(index))
        End Function

        Private Shared Function Selector(name As String) As IntPtr
            Return sel_registerName(name)
        End Function

        Private Shared Function RespondsTo(target As IntPtr, selectorName As String) As Boolean
            If target = IntPtr.Zero Then Return False
            Return MsgSendBoolReturn(target, Selector("respondsToSelector:"), Selector(selectorName))
        End Function

        ' ── Native Bindung ───────────────────────────────────────────────────────
        '
        ' objc_msgSend hat keine feste Signatur: sie richtet sich nach der gerufenen Methode, und
        ' die Laufzeit muss die Argumente in den richtigen Registern uebergeben. Deshalb je eine
        ' eigene Deklaration pro Form statt einer allgemeinen - ein falsch deklariertes Argument
        ' faellt hier nicht auf, sondern erst als Absturz beim Nutzer.

        <DllImport(ObjCRuntime, CallingConvention:=CallingConvention.Cdecl)>
        Private Shared Function sel_registerName(name As String) As IntPtr
        End Function

        <DllImport(ObjCRuntime, CallingConvention:=CallingConvention.Cdecl)>
        Private Shared Function objc_getClass(name As String) As IntPtr
        End Function

        <DllImport(ObjCRuntime, EntryPoint:="objc_msgSend", CallingConvention:=CallingConvention.Cdecl)>
        Private Shared Function MsgSend(receiver As IntPtr, selector As IntPtr) As IntPtr
        End Function

        <DllImport(ObjCRuntime, EntryPoint:="objc_msgSend", CallingConvention:=CallingConvention.Cdecl)>
        Private Shared Sub MsgSendPtrArg(receiver As IntPtr, selector As IntPtr, argument As IntPtr)
        End Sub

        <DllImport(ObjCRuntime, EntryPoint:="objc_msgSend", CallingConvention:=CallingConvention.Cdecl)>
        Private Shared Sub MsgSendBoolArg(receiver As IntPtr, selector As IntPtr,
                                          <MarshalAs(UnmanagedType.I1)> argument As Boolean)
        End Sub

        ' Ein Boolean aus ObjC ist EIN Byte. Als Integer geholt waeren die oberen Bits laut ABI
        ' unbestimmt, und ein Nein mit Muell darueber liese sich als Ja - dieselbe Falle wie im
        ' ImageIO-Weg, deshalb ausdruecklich I1.
        <DllImport(ObjCRuntime, EntryPoint:="objc_msgSend", CallingConvention:=CallingConvention.Cdecl)>
        Private Shared Function MsgSendBoolReturn(receiver As IntPtr, selector As IntPtr,
                                                  argument As IntPtr) As <MarshalAs(UnmanagedType.I1)> Boolean
        End Function

        <DllImport(ObjCRuntime, EntryPoint:="objc_msgSend", CallingConvention:=CallingConvention.Cdecl)>
        Private Shared Function MsgSendBoolReturnNoArg(receiver As IntPtr, selector As IntPtr) _
            As <MarshalAs(UnmanagedType.I1)> Boolean
        End Function

        ' NSUInteger ist auf 64 Bit breit wie ein Zeiger, deshalb IntPtr in beiden Richtungen.
        <DllImport(ObjCRuntime, EntryPoint:="objc_msgSend", CallingConvention:=CallingConvention.Cdecl)>
        Private Shared Function MsgSendUIntReturn(receiver As IntPtr, selector As IntPtr) As IntPtr
        End Function

        <DllImport(ObjCRuntime, EntryPoint:="objc_msgSend", CallingConvention:=CallingConvention.Cdecl)>
        Private Shared Function MsgSendUIntArg(receiver As IntPtr, selector As IntPtr, index As IntPtr) As IntPtr
        End Function

        <DllImport(CoreGraphicsFramework, CallingConvention:=CallingConvention.Cdecl)>
        Private Shared Function CGColorSpaceCreateWithName(name As IntPtr) As IntPtr
        End Function

        <DllImport(CoreGraphicsFramework, CallingConvention:=CallingConvention.Cdecl)>
        Private Shared Sub CGColorSpaceRelease(space As IntPtr)
        End Sub

        <DllImport(CoreGraphicsFramework, CallingConvention:=CallingConvention.Cdecl)>
        Private Shared Function CGColorSpaceEqualToColorSpace(a As IntPtr, b As IntPtr) As <MarshalAs(UnmanagedType.I1)> Boolean
        End Function

        <DllImport(ObjCRuntime, CallingConvention:=CallingConvention.Cdecl)>
        Private Shared Function object_getClassName(target As IntPtr) As IntPtr
        End Function

    End Class

End Namespace
