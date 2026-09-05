Imports System
Imports System.Runtime.InteropServices

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
    ''' <para>EXPERIMENTELL, und der Grund steht hier statt in einer Zusicherung: ob die Ebene den
    ''' Farbraum annimmt, haengt daran, welche Art von Ebene das Toolkit anlegt. Eine
    ''' <c>CAMetalLayer</c> kennt <c>setColorspace:</c>, eine schlichte <c>CALayer</c> nicht. Ohne
    ''' Geraet mit weitem Farbumfang laesst sich das hier nicht pruefen, deshalb ist die Einstellung
    ''' ab Werk aus und jeder Schritt schreibt seinen Ausgang ins Diagnoseprotokoll - wer es
    ''' ausprobiert, kann dort nachsehen, woran es lag.</para>
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

        ''' <summary>Was der letzte Versuch ergeben hat - fuer die Anzeige in den Einstellungen und
        ''' fuer Rueckfragen an jemanden, der es ausprobiert.</summary>
        Public Shared Property LastResult As String = ""

        ''' <summary>Setzt den Farbraum der Ebene hinter diesem Fenster-Handle.</summary>
        ''' <param name="windowHandle">Das Handle aus <c>TopLevel.TryGetPlatformHandle</c>.</param>
        ''' <param name="descriptor">Dessen <c>HandleDescriptor</c>. Erwartet wird NSView oder
        ''' NSWindow; bei einem Fenster wird zuerst dessen Inhaltsansicht geholt.</param>
        ''' <returns>True, wenn der Farbraum tatsaechlich gesetzt wurde.</returns>
        Public Shared Function Apply(windowHandle As IntPtr, descriptor As String) As Boolean
            If Not OperatingSystem.IsMacOS() Then Return False
            If windowHandle = IntPtr.Zero Then
                Report("kein Fenster-Handle")
                Return False
            End If

            Try
                Dim view = windowHandle
                ' Manche Toolkit-Fassungen liefern das NSWindow statt der Ansicht. Der Farbraum
                ' gehoert an die Ebene der ANSICHT, deshalb hier erst der Umweg ueber contentView.
                If String.Equals(descriptor, "NSWindow", StringComparison.OrdinalIgnoreCase) Then
                    view = MsgSend(windowHandle, Selector("contentView"))
                    If view = IntPtr.Zero Then
                        Report("NSWindow ohne contentView")
                        Return False
                    End If
                End If

                ' Eine Ansicht MUSS keine Ebene haben. wantsLayer erzwingt sie; ohne diesen Schritt
                ' liefert layer bei einer ebenenlosen Ansicht schlicht null.
                If RespondsTo(view, "setWantsLayer:") Then MsgSendBoolArg(view, Selector("setWantsLayer:"), True)

                Dim layer = MsgSend(view, Selector("layer"))
                If layer = IntPtr.Zero Then
                    Report("die Ansicht hat keine Ebene")
                    Return False
                End If

                ' DIE entscheidende Stelle: eine CAMetalLayer kennt setColorspace:, eine schlichte
                ' CALayer nicht. Gefragt wird das Objekt selbst, statt seinen Typ zu raten.
                If Not RespondsTo(layer, "setColorspace:") Then
                    Report("die Ebene kennt setColorspace: nicht - dieser Weg traegt hier nicht")
                    Return False
                End If

                Dim srgb = CreateSrgbColorSpace()
                If srgb = IntPtr.Zero Then
                    Report("sRGB-Farbraum liess sich nicht anlegen")
                    Return False
                End If

                Try
                    MsgSendPtrArg(layer, Selector("setColorspace:"), srgb)
                Finally
                    ' Die Ebene haelt den Farbraum selbst fest; unsere Zaehlung geht zurueck.
                    CGColorSpaceRelease(srgb)
                End Try

                Report("Farbraum sRGB an der Ebene gesetzt")
                Return True
            Catch ex As Exception
                Report("fehlgeschlagen: " & ex.Message)
                DiagnosticLogService.LogException("Farbraum.Fenster", ex)
                Return False
            End Try
        End Function

        Private Shared Sub Report(text As String)
            LastResult = text
            DiagnosticLogService.LogAlways("Farbraum", "Fensterfarbraum: " & text)
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

        <DllImport(CoreGraphicsFramework, CallingConvention:=CallingConvention.Cdecl)>
        Private Shared Function CGColorSpaceCreateWithName(name As IntPtr) As IntPtr
        End Function

        <DllImport(CoreGraphicsFramework, CallingConvention:=CallingConvention.Cdecl)>
        Private Shared Sub CGColorSpaceRelease(space As IntPtr)
        End Sub

    End Class

End Namespace
