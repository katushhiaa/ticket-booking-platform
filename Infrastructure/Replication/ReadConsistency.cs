namespace TicketBooking.Api.Infrastructure.Replication;

/// <summary>Вимога бізнес-сценарію до узгодженості читання (ЛР6, п. 2.3).</summary>
public enum ReadConsistency
{
    /// <summary>Допускає застарілі дані (каталог, публічні списки): читання з репліки.</summary>
    Eventual,

    /// <summary>
    /// Read-Your-Own-Writes: користувач має бачити власні щойно внесені зміни.
    /// Репліка використовується, лише якщо вона вже програла WAL до LSN останнього запису цього користувача,
    /// інакше читання йде на Primary.
    /// </summary>
    ReadYourWrites,

    /// <summary>Строга узгодженість (автентифікація, статус оплати): завжди Primary.</summary>
    Strong
}
