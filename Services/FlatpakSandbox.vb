Imports System
Imports System.Collections.Generic
Imports System.IO
Imports System.Linq

Namespace Services

    ''' <summary>Wie die Sandbox des Flatpak einen Ordner sieht.</summary>
    Public Enum SandboxAccess
        ''' <summary>Der Ordner liegt auf dem echten Dateisystem und laesst sich beschreiben. Ausserhalb
        ''' des Flatpak gilt das fuer jeden Pfad: die Frage stellt sich dort nicht.</summary>
        Writable
        ''' <summary>Freigegeben, aber nur zum Lesen (":ro" in der Freigabe).</summary>
        ReadOnlyShare
        ''' <summary>Nicht freigegeben. Den Pfad kann es in der Sandbox trotzdem geben, der
        ''' persoenliche Ordner etwa ist dort immer da; er liegt dann aber in einem Dateisystem der
        ''' Sandbox, und was darin entsteht, ist nach dem Beenden weg.</summary>
        NotShared
    End Enum

    ''' <summary>Welche Ordner die Sandbox des Flatpak dauerhaft sieht, und ob ein Schreibziel
    ''' dazugehoert.
    '''
    ''' <para>DER FEHLER, GEGEN DEN DAS STEHT (GitHub #166): Das Flathub-Manifest gibt nicht den
    ''' ganzen persoenlichen Ordner frei, sondern Bilder, Videos, Schreibtisch, Downloads und die
    ''' Laufwerke. Den Pfad des persoenlichen Ordners legt Flatpak in der Sandbox trotzdem an, als
    ''' leeres Dateisystem im Speicher; nur die Freigaben sind darin eingehaengt. Ein Ordner wie
    ''' ~/2026 laesst sich dort anlegen und beschreiben, jeder Schreibtest gelingt, die Galerie
    ''' zeigt die Bilder, und nach dem Beenden ist alles weg. Auf dem Rechner selbst entsteht der
    ''' Ordner nie.</para>
    '''
    ''' <para>QUELLE DER WAHRHEIT ist /.flatpak-info, Abschnitt [Context], Schluessel filesystems.
    ''' Dort steht die fertige Liste dieses Laufs: das Manifest, dazu Freigaben aus Flatseal oder
    ''' "flatpak override" und aus der Befehlszeile, zurueckgenommene schon herausgerechnet. Eine
    ''' Freigabe ueber den Ordnerwaehler (Dokument-Portal) steht nicht darin; die liegt unter
    ''' /run/user/UID/doc und schreibt auf den echten Ordner durch, auch in neu angelegte
    ''' Unterordner.</para>
    '''
    ''' <para>Nur im Flatpak aktiv, erkannt an FLATPAK_ID wie beim Update-Check. Ohne ist jeder Pfad
    ''' <see cref="SandboxAccess.Writable"/>. Fuer den Pruefstand lassen sich Kennung, Pfad der
    ''' Datei und die Datei der Benutzerordner umlenken.</para></summary>
    Public NotInheritable Class FlatpakSandbox
        Private Sub New()
        End Sub

        Public Const DefaultInfoPath As String = "/.flatpak-info"

        ''' <summary>Was die Freigabe "host" NICHT umfasst: die Ordner, die Flatpak in der Sandbox
        ''' aus der Laufzeit oder als eigenes Dateisystem fuellt. /run/media ist die Ausnahme darin
        ''' (<see cref="IsCoveredByHost"/>), der persoenliche Ordner kommt mit "host" ausdruecklich
        ''' dazu, auch wenn er unter /var liegt.</summary>
        Private Shared ReadOnly _hostReserved As String() = {
            "/app", "/bin", "/boot", "/dev", "/etc", "/lib", "/lib32", "/lib64", "/proc", "/root",
            "/run", "/sbin", "/sys", "/tmp", "/usr", "/var"
        }

        ''' <summary>Eine Freigabe: der Ordner und ob nur gelesen werden darf. "host" steht als
        ''' eigener Eintrag ohne Ordner, weil er alles ausser <see cref="_hostReserved"/> meint.</summary>
        Private NotInheritable Class FilesystemGrant
            Public Property Folder As String = ""
            Public Property ResolvedFolder As String = ""
            Public Property IsReadOnly As Boolean
            Public Property IsHost As Boolean
        End Class

        Private Shared ReadOnly _gate As New Object()
        Private Shared _appId As String = Environment.GetEnvironmentVariable("FLATPAK_ID")
        Private Shared _infoPath As String = DefaultInfoPath
        Private Shared _userDirsPath As String = ""
        Private Shared _grants As List(Of FilesystemGrant)

        ''' <summary>Die Kennung der Anwendung im Flatpak, leer ausserhalb. Ab Werk aus FLATPAK_ID.</summary>
        Public Shared Property AppId As String
            Get
                Return If(_appId, "")
            End Get
            Set(value As String)
                SyncLock _gate
                    _appId = value
                    _grants = Nothing
                End SyncLock
            End Set
        End Property

        ''' <summary>Wo die Beschreibung des Laufs liegt; ab Werk /.flatpak-info.</summary>
        Public Shared Property InfoPath As String
            Get
                Return _infoPath
            End Get
            Set(value As String)
                SyncLock _gate
                    _infoPath = If(String.IsNullOrWhiteSpace(value), DefaultInfoPath, value)
                    _grants = Nothing
                End SyncLock
            End Set
        End Property

        ''' <summary>Die Datei der Benutzerordner (user-dirs.dirs). Leer heisst: die der Sandbox unter
        ''' XDG_CONFIG_HOME. Flatpak legt sie dort selbst an und traegt genau die Benutzerordner ein,
        ''' die freigegeben sind, mit ihren Pfaden auf dem Rechner (~/Bilder statt ~/Pictures).</summary>
        Public Shared Property UserDirsPath As String
            Get
                Return _userDirsPath
            End Get
            Set(value As String)
                SyncLock _gate
                    _userDirsPath = If(value, "")
                    _grants = Nothing
                End SyncLock
            End Set
        End Property

        ''' <summary>Alles wieder wie beim Start: Kennung aus der Umgebung, Datei ab Werk.</summary>
        Public Shared Sub ResetToEnvironment()
            SyncLock _gate
                _appId = Environment.GetEnvironmentVariable("FLATPAK_ID")
                _infoPath = DefaultInfoPath
                _userDirsPath = ""
                _grants = Nothing
            End SyncLock
        End Sub

        Public Shared ReadOnly Property IsActive As Boolean
            Get
                Return Not OperatingSystem.IsWindows() AndAlso Not String.IsNullOrEmpty(_appId)
            End Get
        End Property

        ''' <summary>Wie die Sandbox den Ordner sieht. Ein leerer Pfad, ein relativer und die Ziele
        ''' der Serverquellen (immich://) sind nicht die Frage hier und gelten als
        ''' <see cref="SandboxAccess.Writable"/>; geprueft wird, was die Aufrufer ohnehin pruefen.
        '''
        ''' <para>Entscheidend ist, wo die Daten LANDEN: der Pfad wird mit aufgeloesten Verweisen
        ''' gegen die Freigaben gehalten, die Freigaben wie geschrieben und aufgeloest. Ein Verweis
        ''' aus einer Freigabe in den leeren persoenlichen Ordner der Sandbox ist damit nicht
        ''' freigegeben, ein Verweis darin auf eine Freigabe schon.</para></summary>
        Public Shared Function GetAccess(path As String) As SandboxAccess
            If Not IsActive Then Return SandboxAccess.Writable
            Dim full = FullPathOrEmpty(path)
            If full.Length = 0 Then Return SandboxAccess.Writable
            Dim resolved = FileOperationPolicy.ResolveLinks(full)
            If String.IsNullOrEmpty(resolved) Then resolved = full
            Return Classify(resolved)
        End Function

        ''' <summary>Darf ein Schreibziel hier liegen? Nur bei <see cref="SandboxAccess.Writable"/>.</summary>
        Public Shared Function IsPersistentWriteTarget(path As String) As Boolean
            Return GetAccess(path) = SandboxAccess.Writable
        End Function

        ''' <summary>Die Meldung fuer ein Schreibziel, das die Sandbox nicht dauerhaft beschreiben
        ''' kann, oder leer, wenn es geht. Genannt werden der Ordner und der Weg dorthin: Ordner
        ''' ueber die Ordnerauswahl waehlen, einen unter Bilder nehmen oder dauerhaft freigeben.</summary>
        ''' <param name="portalHelps">False, wo ein Ordner aus der Ordnerauswahl nicht taugt (der
        ''' Datenordner: dessen Pfad muss auch nach einem Neustart noch gelten). Dann nennt die
        ''' Meldung nur Bilder, ein Laufwerk und das dauerhafte Freigeben.</param>
        Public Shared Function WriteDenial(folder As String, Optional portalHelps As Boolean = True) As String
            Select Case GetAccess(folder)
                Case SandboxAccess.NotShared
                    If Not portalHelps Then
                        Return String.Format(LocalizationService.T("Der Ordner {0} ist für FerrumPix als Flatpak nicht freigegeben. Was dort liegt, wäre nach dem Beenden verloren. Bitte einen Ordner unter Bilder oder auf einem eingehängten Laufwerk wählen oder den Ordner dauerhaft freigeben, mit Flatseal oder im Terminal mit {1} und danach FerrumPix neu starten."),
                                             folder, OverrideCommand(SuggestedShare(folder)))
                    End If
                    Return String.Format(LocalizationService.T("Der Ordner {0} ist für FerrumPix als Flatpak nicht freigegeben. Was dort angelegt wird, liegt nur in der Sandbox und ist nach dem Beenden verloren, deshalb schreibt FerrumPix dort nichts. Abhilfe: den Ordner über die Ordnerauswahl wählen, einen Ordner unter Bilder nehmen oder den Ordner dauerhaft freigeben, mit Flatseal oder im Terminal mit {1} und danach FerrumPix neu starten."),
                                         folder, OverrideCommand(SuggestedShare(folder)))
                Case SandboxAccess.ReadOnlyShare
                    Return String.Format(LocalizationService.T("Der Ordner {0} ist für FerrumPix als Flatpak nur zum Lesen freigegeben, schreiben lässt sich dort nicht. Abhilfe: einen anderen Ordner wählen oder die Freigabe erweitern, mit Flatseal oder im Terminal mit {1} und danach FerrumPix neu starten."),
                                         folder, OverrideCommand(folder))
                Case Else
                    Return ""
            End Select
        End Function

        ''' <summary>Welcher Ordner sich zum Freigeben anbietet: der oberste nicht freigegebene ueber
        ''' dem Ziel, also ~/2026 fuer ~/2026/2026-11-27. Der persoenliche Ordner selbst und die
        ''' Wurzel werden nie vorgeschlagen.</summary>
        Friend Shared Function SuggestedShare(folder As String) As String
            Dim current = FullPathOrEmpty(folder)
            If current.Length = 0 Then Return If(folder, "")
            Dim home = FullPathOrEmpty(FileOperationPolicy.PersonalFolder)
            While True
                Dim parent = IO.Path.GetDirectoryName(current)
                If String.IsNullOrEmpty(parent) OrElse PathIdentity.IsFilesystemRoot(parent) Then Exit While
                If home.Length > 0 AndAlso PathIdentity.AreSame(parent, home) Then Exit While
                If GetAccess(parent) <> SandboxAccess.NotShared Then Exit While
                current = parent
            End While
            Return current
        End Function

        ''' <summary>Der Befehl zum dauerhaften Freigeben: im persoenlichen Ordner in der Kurzform
        ''' ~/..., die Flatpak selbst aufloest, sonst absolut. Mit Leerzeichen oder anderen Zeichen,
        ''' die die Shell deutet, in einfachen Anfuehrungszeichen.</summary>
        Friend Shared Function OverrideCommand(folder As String) As String
            Dim full = FullPathOrEmpty(folder)
            If full.Length = 0 Then full = If(folder, "")
            Dim home = FullPathOrEmpty(FileOperationPolicy.PersonalFolder)
            Dim spec = full
            If home.Length > 0 AndAlso PathIdentity.AreSame(full, home) Then
                spec = "home"
            ElseIf home.Length > 0 AndAlso PathIdentity.IsAncestorOrSelf(home, full) Then
                spec = "~/" & IO.Path.GetRelativePath(home, full).Replace(IO.Path.DirectorySeparatorChar, "/"c)
            End If
            Dim argument = "--filesystem=" & spec
            If argument.Any(Function(c) Char.IsWhiteSpace(c) OrElse "'""$`\!*?[]()&;|<>#".IndexOf(c) >= 0) Then
                argument = "'" & argument.Replace("'", "'\''") & "'"
            End If
            Dim id = If(String.IsNullOrEmpty(AppId), "io.github.bitpainter75.ferrumpix", AppId)
            Return "flatpak override --user " & argument & " " & id
        End Function

        Private Shared Function Classify(full As String) As SandboxAccess
            ' Das Dokument-Portal schreibt auf den echten Ordner durch, ab der Ebene doc/KENNUNG/ORDNER.
            For Each portal In DocumentPortalRoots()
                If PathIdentity.IsAncestorOrSelf(portal, full) Then
                    Return If(LevelsBelow(portal, full) >= 2, SandboxAccess.Writable, SandboxAccess.NotShared)
                End If
            Next
            ' Der eigene Datenbereich (~/.var/app/KENNUNG) liegt immer auf dem Rechner.
            Dim own = OwnDataArea()
            If own.Length > 0 AndAlso PathIdentity.IsAncestorOrSelf(own, full) Then Return SandboxAccess.Writable

            Dim best As FilesystemGrant = Nothing
            Dim bestLength = -1
            For Each grant In Grants()
                Dim length As Integer
                If grant.IsHost Then
                    If Not IsCoveredByHost(full) Then Continue For
                    length = 1
                ElseIf PathIdentity.IsAncestorOrSelf(grant.Folder, full) Then
                    length = grant.Folder.Length
                ElseIf grant.ResolvedFolder.Length > 0 AndAlso PathIdentity.IsAncestorOrSelf(grant.ResolvedFolder, full) Then
                    length = grant.ResolvedFolder.Length
                Else
                    Continue For
                End If
                ' Die engste Freigabe gilt, wie beim Einhaengen: ~/Bilder:ro unter home bleibt lesbar.
                ' Bei gleicher Laenge gewinnt die zum Lesen, die vorsichtigere Antwort.
                If length > bestLength OrElse (length = bestLength AndAlso grant.IsReadOnly) Then
                    best = grant
                    bestLength = length
                End If
            Next
            If best Is Nothing Then Return SandboxAccess.NotShared
            Return If(best.IsReadOnly, SandboxAccess.ReadOnlyShare, SandboxAccess.Writable)
        End Function

        ''' <summary>Deckt "host" den Pfad? Alles ausser den Ordnern der Sandbox, /run/media aber
        ''' ja, und der persoenliche Ordner immer.</summary>
        Private Shared Function IsCoveredByHost(full As String) As Boolean
            Dim home = FullPathOrEmpty(FileOperationPolicy.PersonalFolder)
            If home.Length > 0 AndAlso PathIdentity.IsAncestorOrSelf(home, full) Then Return True
            If PathIdentity.IsAncestorOrSelf("/run/media", full) Then Return True
            If PathIdentity.IsFilesystemRoot(full) Then Return False
            Return Not _hostReserved.Any(Function(r) PathIdentity.IsAncestorOrSelf(r, full))
        End Function

        Private Shared Function LevelsBelow(root As String, full As String) As Integer
            Dim relative = IO.Path.GetRelativePath(root, full)
            If relative = "." Then Return 0
            Return relative.Split(IO.Path.DirectorySeparatorChar, StringSplitOptions.RemoveEmptyEntries).Length
        End Function

        Private Shared Function DocumentPortalRoots() As List(Of String)
            Dim roots As New List(Of String)()
            Dim runtimeDir = Environment.GetEnvironmentVariable("XDG_RUNTIME_DIR")
            For Each candidate In {FileOperationPolicy.DocumentPortalRoot,
                                   If(String.IsNullOrEmpty(runtimeDir), "", IO.Path.Combine(runtimeDir, "doc")),
                                   "/run/flatpak/doc"}
                Dim full = FullPathOrEmpty(candidate)
                If full.Length > 0 AndAlso Not roots.Any(Function(r) PathIdentity.AreSame(r, full)) Then roots.Add(full)
            Next
            Return roots
        End Function

        Private Shared Function OwnDataArea() As String
            If String.IsNullOrEmpty(AppId) Then Return ""
            Return FullPathOrEmpty(IO.Path.Combine(FileOperationPolicy.PersonalFolder, ".var", "app", AppId))
        End Function

        Private Shared Function Grants() As List(Of FilesystemGrant)
            SyncLock _gate
                If _grants Is Nothing Then _grants = LoadGrants()
                Return _grants
            End SyncLock
        End Function

        ''' <summary>Liest filesystems aus [Context]. Fehlt die Datei oder der Schluessel, gibt es
        ''' keine Freigabe: dann gelten nur Portal und eigener Datenbereich, die vorsichtige Seite.</summary>
        Private Shared Function LoadGrants() As List(Of FilesystemGrant)
            Dim value = ""
            Try
                If File.Exists(_infoPath) Then value = ReadContextFilesystems(File.ReadAllLines(_infoPath))
            Catch ex As Exception
                DiagnosticLogService.LogException("Flatpak.Info", ex)
            End Try
            Dim result = ParseFilesystems(value)
            DiagnosticLogService.LogAlways("Flatpak.Filesystems",
                $"info={_infoPath} filesystems={value} grants={String.Join(";", result.Select(Function(g) If(g.IsHost, "host", g.Folder) & If(g.IsReadOnly, ":ro", "")))}")
            Return result
        End Function

        ''' <summary>Der Wert von filesystems im Abschnitt [Context]. Die Datei kann weitere
        ''' Abschnitte mit demselben Schluessel tragen; nur [Context] zaehlt.</summary>
        Friend Shared Function ReadContextFilesystems(lines As IEnumerable(Of String)) As String
            Dim inContext = False
            For Each raw In lines
                Dim line = If(raw, "").Trim()
                If line.StartsWith("[", StringComparison.Ordinal) Then
                    inContext = String.Equals(line, "[Context]", StringComparison.Ordinal)
                    Continue For
                End If
                If Not inContext Then Continue For
                Dim separator = line.IndexOf("="c)
                If separator <= 0 Then Continue For
                If String.Equals(line.Substring(0, separator).Trim(), "filesystems", StringComparison.Ordinal) Then
                    Return line.Substring(separator + 1).Trim()
                End If
            Next
            Return ""
        End Function

        ''' <summary>Zerlegt eine filesystems-Zeile wie "xdg-pictures;~/2026:ro;/media;home;". Ein
        ''' Eintrag mit "!" vorn ist eine Ruecknahme und gibt nichts frei. Unbekannte Eintraege fallen
        ''' weg, ebenso Benutzerordner, die sich nicht bestimmen lassen: lieber eine Warnung zu viel
        ''' als ein Ziel, das beim Beenden verschwindet.</summary>
        Private Shared Function ParseFilesystems(value As String) As List(Of FilesystemGrant)
            Dim result As New List(Of FilesystemGrant)()
            If String.IsNullOrWhiteSpace(value) Then Return result
            Dim home = FullPathOrEmpty(FileOperationPolicy.PersonalFolder)
            Dim userDirs = ReadUserDirs(home)
            For Each rawEntry In value.Split(";"c, StringSplitOptions.RemoveEmptyEntries Or StringSplitOptions.TrimEntries)
                If rawEntry.StartsWith("!", StringComparison.Ordinal) Then Continue For
                Dim entry = rawEntry
                Dim isReadOnly = False
                Dim colon = entry.LastIndexOf(":"c)
                If colon > 0 Then
                    Dim mode = entry.Substring(colon + 1)
                    If mode = "ro" OrElse mode = "rw" OrElse mode = "create" Then
                        isReadOnly = (mode = "ro")
                        entry = entry.Substring(0, colon)
                    End If
                End If
                If entry = "host" Then
                    result.Add(New FilesystemGrant With {.IsHost = True, .IsReadOnly = isReadOnly})
                    Continue For
                End If
                Dim folder = ResolveEntry(entry, home, userDirs)
                If folder.Length = 0 OrElse PathIdentity.IsFilesystemRoot(folder) Then Continue For
                Dim resolved = FileOperationPolicy.ResolveLinks(folder)
                result.Add(New FilesystemGrant With {
                    .Folder = folder,
                    .ResolvedFolder = If(String.IsNullOrEmpty(resolved) OrElse PathIdentity.AreSame(resolved, folder), "", resolved),
                    .IsReadOnly = isReadOnly})
            Next
            Return result
        End Function

        ''' <summary>Ein Eintrag als Ordner, oder leer. "host-os" und "host-etc" geben nur Ordner des
        ''' Systems frei und sind nie ein Schreibziel; sie fallen hier weg.</summary>
        Private Shared Function ResolveEntry(entry As String, home As String, userDirs As Dictionary(Of String, String)) As String
            If entry.StartsWith("/", StringComparison.Ordinal) Then Return FullPathOrEmpty(entry)
            If home.Length = 0 Then Return ""
            If entry = "home" OrElse entry = "~" Then Return home
            If entry.StartsWith("~/", StringComparison.Ordinal) Then Return FullPathOrEmpty(IO.Path.Combine(home, entry.Substring(2)))
            If entry.StartsWith("home/", StringComparison.Ordinal) Then Return FullPathOrEmpty(IO.Path.Combine(home, entry.Substring(5)))
            If Not entry.StartsWith("xdg-", StringComparison.Ordinal) Then Return ""
            Dim slash = entry.IndexOf("/"c)
            Dim name = If(slash < 0, entry, entry.Substring(0, slash))
            Dim subPath = If(slash < 0, "", entry.Substring(slash + 1))
            Dim baseFolder As String = ""
            Select Case name
                Case "xdg-config" : baseFolder = IO.Path.Combine(home, ".config")
                Case "xdg-data" : baseFolder = IO.Path.Combine(home, ".local", "share")
                Case "xdg-cache" : baseFolder = IO.Path.Combine(home, ".cache")
                Case "xdg-state" : baseFolder = IO.Path.Combine(home, ".local", "state")
                Case "xdg-run"
                    Dim runtimeDir = Environment.GetEnvironmentVariable("XDG_RUNTIME_DIR")
                    If String.IsNullOrEmpty(runtimeDir) Then Return ""
                    baseFolder = runtimeDir
                Case Else
                    Dim key = UserDirKey(name)
                    If key.Length = 0 OrElse userDirs Is Nothing OrElse Not userDirs.TryGetValue(key, baseFolder) Then Return ""
                    ' Zeigt ein Benutzerordner auf den persoenlichen Ordner selbst, gibt Flatpak ihn
                    ' nicht frei; sonst waere ein schlecht gepflegtes user-dirs.dirs der ganze Ordner.
                    If PathIdentity.AreSame(baseFolder, home) Then Return ""
            End Select
            Return FullPathOrEmpty(If(subPath.Length = 0, baseFolder, IO.Path.Combine(baseFolder, subPath)))
        End Function

        Private Shared Function UserDirKey(name As String) As String
            Select Case name
                Case "xdg-desktop" : Return "DESKTOP"
                Case "xdg-documents" : Return "DOCUMENTS"
                Case "xdg-download" : Return "DOWNLOAD"
                Case "xdg-music" : Return "MUSIC"
                Case "xdg-pictures" : Return "PICTURES"
                Case "xdg-public-share" : Return "PUBLICSHARE"
                Case "xdg-templates" : Return "TEMPLATES"
                Case "xdg-videos" : Return "VIDEOS"
                Case Else : Return ""
            End Select
        End Function

        ''' <summary>Die Benutzerordner aus user-dirs.dirs, Schluessel ohne XDG_ und _DIR. Laesst
        ''' sich die Datei nicht lesen, gelten die Namen von .NET fuer Bilder, Videos, Schreibtisch,
        ''' Dokumente und Musik und ~/Downloads; steht ein Ordner in einer gelesenen Datei nicht,
        ''' ist er nicht freigegeben (Flatpak schreibt dort genau die freigegebenen hinein).</summary>
        Private Shared Function ReadUserDirs(home As String) As Dictionary(Of String, String)
            Dim result As New Dictionary(Of String, String)(StringComparer.Ordinal)
            Dim dirsFile = _userDirsPath
            If String.IsNullOrEmpty(dirsFile) Then
                Dim configHome = Environment.GetEnvironmentVariable("XDG_CONFIG_HOME")
                If String.IsNullOrEmpty(configHome) Then configHome = IO.Path.Combine(home, ".config")
                dirsFile = IO.Path.Combine(configHome, "user-dirs.dirs")
            End If
            Try
                If IO.File.Exists(dirsFile) Then
                    For Each raw In IO.File.ReadAllLines(dirsFile)
                        Dim line = raw.Trim()
                        If line.StartsWith("#", StringComparison.Ordinal) OrElse Not line.StartsWith("XDG_", StringComparison.Ordinal) Then Continue For
                        Dim separator = line.IndexOf("="c)
                        If separator <= 0 Then Continue For
                        Dim key = line.Substring(4, separator - 4)
                        If Not key.EndsWith("_DIR", StringComparison.Ordinal) Then Continue For
                        key = key.Substring(0, key.Length - 4)
                        Dim folder = line.Substring(separator + 1).Trim().Trim(""""c)
                        If folder.StartsWith("$HOME", StringComparison.Ordinal) Then folder = home & folder.Substring(5)
                        If Not folder.StartsWith("/", StringComparison.Ordinal) Then Continue For
                        result(key) = FullPathOrEmpty(folder)
                    Next
                    Return result
                End If
            Catch ex As Exception
                DiagnosticLogService.LogException("Flatpak.UserDirs", ex)
                result.Clear()
            End Try
            result("DESKTOP") = Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory)
            result("DOCUMENTS") = Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments)
            result("MUSIC") = Environment.GetFolderPath(Environment.SpecialFolder.MyMusic)
            result("PICTURES") = Environment.GetFolderPath(Environment.SpecialFolder.MyPictures)
            result("VIDEOS") = Environment.GetFolderPath(Environment.SpecialFolder.MyVideos)
            result("DOWNLOAD") = IO.Path.Combine(home, "Downloads")
            For Each key In result.Keys.ToList()
                If String.IsNullOrEmpty(result(key)) Then result.Remove(key)
            Next
            Return result
        End Function

        Private Shared Function FullPathOrEmpty(path As String) As String
            If String.IsNullOrWhiteSpace(path) OrElse path.Contains("://", StringComparison.Ordinal) Then Return ""
            Try
                If Not IO.Path.IsPathRooted(path) Then Return ""
                Dim full = IO.Path.GetFullPath(path)
                If PathIdentity.IsFilesystemRoot(full) Then Return full
                Return full.TrimEnd(IO.Path.DirectorySeparatorChar, IO.Path.AltDirectorySeparatorChar)
            Catch
                Return ""
            End Try
        End Function

    End Class

End Namespace
