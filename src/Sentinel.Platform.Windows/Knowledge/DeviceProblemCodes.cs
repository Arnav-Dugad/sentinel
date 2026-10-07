namespace Sentinel.Platform.Windows.Knowledge;

/// <summary>Device Manager problem codes (CM_PROB_*) with plain-language explanations.</summary>
public static class DeviceProblemCodes
{
    public static string Describe(int code) => code switch
    {
        1 => "The device is not configured correctly (code 1).",
        3 => "The driver may be corrupted, or the system is low on memory or other resources (code 3).",
        10 => "The device cannot start (code 10). This often follows a driver problem or a hardware fault.",
        12 => "The device cannot find enough free resources to use (code 12).",
        14 => "The device needs a restart to finish setting up (code 14).",
        18 => "The drivers for this device need to be reinstalled (code 18).",
        19 => "Windows cannot start the device because its configuration information is incomplete or damaged (code 19).",
        21 => "Windows is removing this device (code 21).",
        24 => "The device is not present, is not working properly, or does not have all its drivers installed (code 24).",
        28 => "The drivers for this device are not installed (code 28).",
        29 => "The device is disabled because its firmware did not provide the required resources (code 29).",
        31 => "The device is not working properly because Windows cannot load the required drivers (code 31).",
        32 => "A driver service for this device has been disabled (code 32).",
        34 => "Windows cannot determine the resources for this device (code 34).",
        37 => "Windows cannot initialize the device driver (code 37).",
        38 => "A previous instance of the device driver is still in memory (code 38).",
        39 => "The driver may be corrupted or missing (code 39).",
        40 => "Driver service registry information is missing or incorrect (code 40).",
        41 => "Windows loaded the driver but cannot find the hardware device (code 41).",
        43 => "Windows stopped this device because it reported problems (code 43).",
        47 => "The device was prepared for safe removal but has not been removed (code 47).",
        48 => "The driver was blocked from starting because it has known problems with Windows (code 48).",
        49 => "Windows cannot start new hardware devices because the system hive is too large (code 49).",
        52 => "Windows cannot verify the digital signature for the drivers (code 52).",
        _ => $"Device Manager problem code {code}.",
    };
}
