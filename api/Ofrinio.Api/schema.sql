-- Schema creation for Ofrinio Calendar and Bookings

-- 1. Availability Table
IF NOT EXISTS (SELECT * FROM sys.objects WHERE object_id = OBJECT_ID(N'[dbo].[Availability]') AND type in (N'U'))
BEGIN
    CREATE TABLE dbo.Availability (
        Date DATE PRIMARY KEY,
        Status NVARCHAR(50) NOT NULL CHECK (Status IN ('free', 'booked', 'pending')),
        GuestName NVARCHAR(255) NULL,
        Phone NVARCHAR(50) NULL,
        Notes NVARCHAR(MAX) NULL,
        Source NVARCHAR(100) NULL,
        UpdatedAt DATETIME2 NOT NULL DEFAULT SYSUTCDATETIME()
    );

    -- Seed initial availability copied from the Supabase public availability view.
    INSERT INTO dbo.Availability (Date, Status) VALUES 
    ('2026-06-06', 'booked'),
    ('2026-06-07', 'booked'),
    ('2026-06-08', 'booked'),
    ('2026-06-09', 'booked'),
    ('2026-06-10', 'booked'),
    ('2026-06-11', 'booked'),
    ('2026-06-12', 'booked'),
    ('2026-06-16', 'booked'),
    ('2026-06-17', 'booked'),
    ('2026-06-18', 'booked'),
    ('2026-06-19', 'booked'),
    ('2026-06-21', 'booked'),
    ('2026-06-22', 'booked'),
    ('2026-06-23', 'booked'),
    ('2026-06-24', 'booked'),
    ('2026-06-25', 'booked'),
    ('2026-06-26', 'booked'),
    ('2026-06-27', 'booked'),
    ('2026-06-28', 'booked'),
    ('2026-06-29', 'booked'),
    ('2026-07-01', 'booked'),
    ('2026-07-02', 'booked'),
    ('2026-07-03', 'booked'),
    ('2026-07-04', 'booked'),
    ('2026-07-05', 'booked'),
    ('2026-07-06', 'booked'),
    ('2026-07-07', 'booked'),
    ('2026-07-08', 'booked'),
    ('2026-07-09', 'booked'),
    ('2026-07-10', 'booked'),
    ('2026-07-11', 'booked'),
    ('2026-07-13', 'pending'),
    ('2026-07-14', 'pending'),
    ('2026-07-15', 'pending'),
    ('2026-07-16', 'pending'),
    ('2026-07-17', 'pending'),
    ('2026-07-18', 'pending'),
    ('2026-07-19', 'pending'),
    ('2026-07-20', 'booked'),
    ('2026-07-21', 'booked'),
    ('2026-07-22', 'booked'),
    ('2026-07-23', 'booked'),
    ('2026-07-24', 'booked'),
    ('2026-07-25', 'booked'),
    ('2026-07-26', 'booked'),
    ('2026-07-27', 'booked');
END;

IF COL_LENGTH('dbo.Availability', 'GuestName') IS NULL
BEGIN
    ALTER TABLE dbo.Availability ADD GuestName NVARCHAR(255) NULL;
END;

IF COL_LENGTH('dbo.Availability', 'Phone') IS NULL
BEGIN
    ALTER TABLE dbo.Availability ADD Phone NVARCHAR(50) NULL;
END;

IF COL_LENGTH('dbo.Availability', 'Notes') IS NULL
BEGIN
    ALTER TABLE dbo.Availability ADD Notes NVARCHAR(MAX) NULL;
END;

IF COL_LENGTH('dbo.Availability', 'Source') IS NULL
BEGIN
    ALTER TABLE dbo.Availability ADD Source NVARCHAR(100) NULL;
END;

IF COL_LENGTH('dbo.Availability', 'UpdatedAt') IS NULL
BEGIN
    ALTER TABLE dbo.Availability ADD UpdatedAt DATETIME2 NOT NULL CONSTRAINT DF_Availability_UpdatedAt DEFAULT SYSUTCDATETIME();
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

-- 3. Owner/Admin Users Table
IF NOT EXISTS (SELECT * FROM sys.objects WHERE object_id = OBJECT_ID(N'[dbo].[AdminUsers]') AND type in (N'U'))
BEGIN
    CREATE TABLE dbo.AdminUsers (
        Username NVARCHAR(100) NOT NULL PRIMARY KEY,
        PasswordHash NVARCHAR(500) NOT NULL,
        DisplayName NVARCHAR(255) NULL,
        IsActive BIT NOT NULL DEFAULT 1,
        CreatedAt DATETIME2 NOT NULL DEFAULT SYSUTCDATETIME(),
        UpdatedAt DATETIME2 NOT NULL DEFAULT SYSUTCDATETIME()
    );
END;

-- 4. Application Settings Table
IF NOT EXISTS (SELECT * FROM sys.objects WHERE object_id = OBJECT_ID(N'[dbo].[AppSettings]') AND type in (N'U'))
BEGIN
    CREATE TABLE dbo.AppSettings (
        SettingKey NVARCHAR(100) NOT NULL PRIMARY KEY,
        SettingValue NVARCHAR(1000) NOT NULL,
        UpdatedAt DATETIME2 NOT NULL DEFAULT SYSUTCDATETIME()
    );
END;
