Namespace ViewModels

    ''' <summary>Die Reiter der Infoleiste. "People" steht gleich hinter "General", weil es das
    ''' einzige ist, was der Benutzer dort BEARBEITET - alles danach ist Anzeige. Der Reiter
    ''' erscheint nur, wenn auf dem Bild ueberhaupt jemand erkannt wurde.</summary>
    Public Enum InfoSidebarTab
        General
        People
        Exif
        Iptc
        Xmp
        Icc
    End Enum

End Namespace
