-- Materials whose stock is below a threshold, lowest first.
CREATE OR ALTER PROCEDURE [dbo].[GetLowStock] @threshold int
AS
BEGIN
    SELECT m.Id, m.Name FROM dbo.Materials m WHERE m.Quantity < @threshold ORDER BY m.Quantity
END
GO

CREATE PROCEDURE dbo.ResetStock @id int AS
    UPDATE dbo.Materials SET Quantity = 0 WHERE Id = @id; -- FROM dbo.Ignored in a comment
    EXEC dbo.GetLowStock 1
GO
