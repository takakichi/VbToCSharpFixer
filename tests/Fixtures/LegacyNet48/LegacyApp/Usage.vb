Imports Legacy.Common.Collections

Public Class Usage
    Public Function Read(employees As EmployeeCollection, index As Integer) As String
        Return employees(index).ToString
    End Function
End Class
