Imports System
Imports System.Collections.Generic
Imports System.IO
Imports System.Text.RegularExpressions
Imports Avalonia.Platform

Namespace Services

    ''' <summary>Wo die Symbole liegen, und der Rückweg für alte Pfade.
    '''
    ''' <para>Die Oberfläche und die Symbolgalerie nehmen Phosphor (MIT, Lizenz in
    ''' <c>Assets/Icons/phosphor/LICENSE</c>), in fünf Strichstärken je ein Ordner. Eigene Zeichnungen,
    ''' für die es dort nichts Passendes gibt, liegen unter <c>custom</c>.</para>
    '''
    ''' <para>Bis 0.9.73 kamen die Symbole aus Tabler (<c>Assets/Icons/outline</c>). Der Ordner ist weg,
    ''' gespeicherte Bilder und Projekte können aber noch eingefügte Symbole mit diesem Pfad tragen.
    ''' <see cref="ResolveLegacy"/> biegt sie um: gleicher Name, sonst die Zuordnung unten, sonst
    ''' einfache Namensregeln, sonst ein Fragezeichen. Das Fragezeichen ist Absicht: die Ebene bleibt
    ''' sichtbar und lässt sich ersetzen, statt still zu verschwinden.</para></summary>
    Public NotInheritable Class IconAssets

        Private Sub New()
        End Sub

        Public Const Root As String = "avares://FerrumPix/Assets/Icons/"
        Public Const Regular As String = Root & "phosphor/regular/"
        Private Const LegacyFolder As String = "/Assets/Icons/outline/"
        Private Const Placeholder As String = "question"

        ''' <summary>Tabler-Name nach Phosphor-Name (oder <c>custom/…</c>), nur wo beide verschieden
        ''' heißen. Entstanden beim Umstieg aus allen Symbolen, die die Oberfläche benutzte.</summary>
        Private Shared ReadOnly LegacyNames As New Dictionary(Of String, String)(StringComparer.OrdinalIgnoreCase) From {
            {"abc", "text-aa"}, {"adjustments", "sliders-horizontal"}, {"adjustments-alt", "sliders"},
            {"adjustments-cancel", "eraser"}, {"adjustments-down", "sliders-horizontal"}, {"adjustments-horizontal", "sliders-horizontal"},
            {"adjustments-plus", "plus-circle"}, {"adjustments-share", "share-network"}, {"album", "images-square"},
            {"alert-triangle", "warning"}, {"align-center", "text-align-center"}, {"align-justified", "text-align-justify"},
            {"arrow-back-up", "arrow-u-up-left"}, {"arrows-exchange", "arrows-left-right"}, {"arrows-maximize", "arrows-out"},
            {"arrows-minimize", "arrows-in"}, {"arrows-shuffle", "shuffle"}, {"aspect-ratio", "frame-corners"},
            {"ban", "prohibit"}, {"bandage", "custom/repair-brush"}, {"blur", "drop-half-bottom"},
            {"border-none", "square-split-horizontal"}, {"border-outer", "square"}, {"brand-github", "github-logo"},
            {"brightness", "sun"}, {"brightness-down", "sun-dim"}, {"brightness-up", "sun"},
            {"brush", "paint-brush"}, {"bucket-droplet", "paint-bucket"}, {"chart-histogram", "chart-bar"},
            {"chevron-down", "caret-down"}, {"chevron-left", "caret-left"}, {"chevron-right", "caret-right"},
            {"chevron-up", "caret-up"}, {"circle-check", "check-circle"}, {"circle-half-2", "circle-half"},
            {"circle-plus", "plus-circle"}, {"circle-x", "x-circle"}, {"click", "cursor-click"},
            {"clock-hour-4", "clock"}, {"color-filter", "circles-three"}, {"color-picker", "eyedropper"},
            {"color-swatch", "swatches"}, {"cone", "custom/cone"}, {"contrast", "circle-half-tilt"},
            {"contrast-2", "circle-half-tilt"}, {"corner-left-down", "arrow-elbow-left-down"}, {"cut", "scissors"},
            {"deselect", "selection-slash"}, {"device-floppy", "floppy-disk"}, {"device-sd-card", "custom/memory-card"},
            {"droplet", "drop"}, {"droplet-half", "drop-half"}, {"droplet-minus", "drop-slash"},
            {"droplet-shape", "drop"}, {"edit", "pencil-simple"}, {"eight-point-star", "custom/eight-point-star"},
            {"ellipse-speech-bubble-shape", "chat-circle"}, {"exposure", "plus-minus"}, {"external-link", "arrow-square-out"},
            {"file-export", "export"}, {"file-info", "info"}, {"filter", "funnel"},
            {"flag-check", "thumbs-up"}, {"flag-x", "thumbs-down"}, {"folder-check", "folder-open"},
            {"folder-x", "folder-simple-minus"}, {"frame", "frame-corners"}, {"gradient-linear", "gradient"},
            {"gradient-radial", "custom/gradient-radial"}, {"grain", "dots-nine"}, {"grid-dots", "dots-nine"},
            {"grid-pattern", "grid-four"}, {"history", "clock-counter-clockwise"}, {"home", "house"},
            {"icons", "shapes"}, {"info-circle", "info"}, {"layers-intersect", "intersect"},
            {"layers-selected-bottom", "stack-simple"}, {"layers-subtract", "subtract"}, {"layers-union", "unite"},
            {"layout-align-bottom", "align-bottom"}, {"layout-align-center", "align-center-horizontal"}, {"layout-align-left", "align-left"},
            {"layout-align-middle", "align-center-vertical"}, {"layout-align-right", "align-right"}, {"layout-align-top", "align-top"},
            {"layout-board", "custom/photo-wall"}, {"layout-bottombar", "film-strip"}, {"layout-distribute-horizontal", "columns"},
            {"layout-distribute-vertical", "rows"}, {"layout-grid", "squares-four"}, {"layout-rows", "rows"},
            {"layout-sidebar-left-collapse", "sidebar-simple"}, {"layout-sidebar-left-expand", "sidebar"}, {"license", "certificate"},
            {"line", "line-segment"}, {"line-shape", "line-segment"}, {"lock-square", "lock-simple"},
            {"map", "map-trifold"}, {"map-pin-check", "map-pin-simple"}, {"map-pin-search", "map-pin-area"},
            {"map-pin-share", "copy-simple"}, {"map-pin-x", "eraser"}, {"marquee", "selection"},
            {"marquee-2", "selection-all"}, {"mask", "mask-happy"}, {"maximize", "corners-out"},
            {"menu-2", "list"}, {"message", "chat-centered"}, {"mist", "cloud-fog"},
            {"oval", "circle"}, {"pencil-plus", "pencil-simple-line"}, {"photo", "image"},
            {"photo-edit", "image-square"}, {"photo-minus", "image-broken"}, {"photo-plus", "image"},
            {"photo-scan", "scan"}, {"photo-search", "magnifying-glass"}, {"pin", "push-pin"},
            {"pinned", "push-pin"}, {"player-pause", "pause"}, {"player-play", "play"},
            {"playlist-x", "x-square"}, {"pointer", "cursor"}, {"qrcode", "qr-code"},
            {"refresh", "arrows-clockwise"}, {"replace", "swap"}, {"restore", "arrow-counter-clockwise"},
            {"ripple", "wave-sine"}, {"rotate-2", "arrow-clockwise"}, {"rotate-clockwise", "arrow-clockwise"},
            {"rotate-clockwise-2", "arrow-clockwise"}, {"rotate-rectangle", "arrows-counter-clockwise"}, {"route", "path"},
            {"rubber-stamp", "stamp"}, {"ruler-2", "ruler"}, {"search", "magnifying-glass"},
            {"select-all", "selection-all"}, {"separator-vertical", "line-vertical"}, {"settings", "gear"},
            {"settings-bolt", "gear-six"}, {"shadow", "square-half-bottom"}, {"shape", "shapes"},
            {"sparkles", "sparkle"}, {"speech-bubble-shape", "chat"}, {"square-minus", "minus-square"},
            {"square-plus", "plus-square"}, {"square-rotated", "diamond"}, {"square-rounded", "square"},
            {"square-rounded-plus", "plus-square"}, {"squares", "squares-four"}, {"stack-2", "stack"},
            {"stack-3", "stack-simple"}, {"stack-pop", "stack-minus"}, {"text-size", "text-t"},
            {"tilt-shift", "aperture"}, {"transform", "bounding-box"}, {"trapezoid", "custom/trapezoid"},
            {"typography", "text-aa"}, {"user-x", "user-minus"}, {"vector", "vector-two"},
            {"vector-bezier", "bezier-curve"}, {"vector-bezier-arc", "bezier-curve"}, {"vector-bezier-circle", "circle-dashed"},
            {"volume", "speaker-high"}, {"wand", "magic-wand"}, {"world", "globe"},
            {"zoom-in", "magnifying-glass-plus"}, {"zoom-out", "magnifying-glass-minus"}, {"zoom-reset", "arrows-in-simple"}
        }

        Private Shared _regularNames As HashSet(Of String)
        Private Shared ReadOnly _regularNamesLock As New Object()

        ''' <summary>Die Namen der Phosphor-Symbole in normaler Stärke, einmal eingelesen. Leer, wenn
        ''' der Ressourcenbestand nicht erreichbar ist (Prüfstand ohne Anwendung).</summary>
        Private Shared Function RegularNames() As HashSet(Of String)
            SyncLock _regularNamesLock
                If _regularNames IsNot Nothing Then Return _regularNames
                Dim names As New HashSet(Of String)(StringComparer.OrdinalIgnoreCase)
                Try
                    For Each uri In AssetLoader.GetAssets(New Uri(Regular), Nothing)
                        Dim file = Path.GetFileNameWithoutExtension(uri.AbsolutePath)
                        If Not String.IsNullOrEmpty(file) Then names.Add(file)
                    Next
                Catch
                End Try
                _regularNames = names
                Return names
            End SyncLock
        End Function

        ''' <summary>Ein Pfad in den alten Tabler-Ordner wird auf das passende Symbol umgebogen; jeder
        ''' andere Pfad bleibt, wie er ist.</summary>
        Public Shared Function ResolveLegacy(source As String) As String
            If String.IsNullOrWhiteSpace(source) Then Return source
            Dim index = source.IndexOf(LegacyFolder, StringComparison.OrdinalIgnoreCase)
            If index < 0 Then Return source
            Dim name = Path.GetFileNameWithoutExtension(source.Substring(index + LegacyFolder.Length))
            Return PathFor(LegacyTarget(name))
        End Function

        Friend Shared Function LegacyTarget(name As String) As String
            Dim mapped As String = Nothing
            If LegacyNames.TryGetValue(name, mapped) Then Return mapped
            Dim known = RegularNames()
            ' Ohne Ressourcenbestand lässt sich nichts nachsehen; dann gilt der Name selbst.
            If known.Count = 0 Then Return name
            For Each candidate In {name, Regex.Replace(name, "-\d+$", ""),
                                   Regex.Replace(name, "^circle-(.+)$", "$1-circle"),
                                   Regex.Replace(name, "^square-(.+)$", "$1-square")}
                If known.Contains(candidate) Then Return candidate
            Next
            Return Placeholder
        End Function

        Private Shared Function PathFor(target As String) As String
            If target.StartsWith("custom/", StringComparison.Ordinal) Then Return Root & target & ".svg"
            Return Regular & target & ".svg"
        End Function

    End Class

End Namespace
