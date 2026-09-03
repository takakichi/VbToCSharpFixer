Imports Legacy.Common.Collections
Imports Microsoft.VisualBasic

Public Class Usage
    Public Function Read(employees As EmployeeCollection, index As Integer) As String
        Dim text = employees(index).ToString
        Dim part = Mid(text, 1, 3)
        Dim display = Format(part, "@@@")
        Dim validDate = IsDate(display)
        Dim missing = IsNothing(employees)
        Return display
    End Function
End Class
