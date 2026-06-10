-- Schema creation for Ofrinio Calendar and Bookings

-- 1. Availability Table
IF NOT EXISTS (SELECT * FROM sys.objects WHERE object_id = OBJECT_ID(N'[dbo].[Availability]') AND type in (N'U'))
BEGIN
    CREATE TABLE dbo.Availability (
        Date DATE PRIMARY KEY,
        Status NVARCHAR(50) NOT NULL CHECK (Status IN ('free', 'booked', 'pending'))
    );

    -- Seed some initial fallback data for demo
    INSERT INTO dbo.Availability (Date, Status) VALUES 
    ('2026-06-20', 'pending'),
    ('2026-06-21', 'pending'),
    ('2026-07-05', 'booked'),
    ('2026-07-06', 'booked'),
    ('2026-07-07', 'booked'),
    ('2026-07-08', 'booked'),
    ('2026-08-10', 'booked'),
    ('2026-08-11', 'booked'),
    ('2026-08-12', 'booked'),
    ('2026-09-02', 'pending');
END;

-- 2. Booking Requests Table
IF NOT EXISTS (SELECT * FROM sys.objects WHERE object_id = OBJECT_ID(N'[dbo].[BookingRequests]') AND type in (N'U'))
BEGIN
    CREATE TABLE dbo.BookingRequests (
        Id INT IDENTITY(1,1) PRIMARY KEY,
        Name NVARCHAR(255) NOT NULL,
        Phone NVARCHAR(50) NOT NULL,
        RequestedDates NVARCHAR(1000) NOT NULL,
        Message NVARCHAR(MAX) NULL,
        Source NVARCHAR(50) NOT NULL,
        CreatedAt DATETIME2 DEFAULT SYSUTCDATETIME()
    );
END;
