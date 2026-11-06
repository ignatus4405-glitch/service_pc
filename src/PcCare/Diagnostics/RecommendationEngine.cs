using System;
using System.Collections.Generic;
using PcCare.Models;

namespace PcCare.Diagnostics
{
    /// <summary>
    /// Converts a collected snapshot into explainable, non-destructive recommendations.
    /// It deliberately does not modify Windows settings or claim that an unknown SMART
    /// result means a healthy drive.
    /// </summary>
    public sealed class RecommendationEngine
    {
        public IList<Recommendation> Create(SystemSnapshot snapshot)
        {
            if (snapshot == null)
                throw new ArgumentNullException("snapshot");

            List<Recommendation> recommendations = new List<Recommendation>();

            foreach (PhysicalDiskInfo disk in snapshot.PhysicalDisks)
            {
                string diskStatus = (disk.Status ?? String.Empty).Trim();
                if (IsConcerningDiskStatus(diskStatus))
                {
                    RecommendationSeverity diskSeverity =
                        String.Equals(diskStatus, "Pred Fail", StringComparison.OrdinalIgnoreCase) ||
                        String.Equals(diskStatus, "Error", StringComparison.OrdinalIgnoreCase)
                            ? RecommendationSeverity.Critical
                            : RecommendationSeverity.Warning;
                    recommendations.Add(new Recommendation(
                        diskSeverity,
                        "Windows сообщает о проблеме накопителя",
                        "Диск " + disk.Index + " (" + (String.IsNullOrWhiteSpace(disk.Model) ? "модель не указана" : disk.Model) +
                        ") имеет статус «" + diskStatus + "». Сохраните важные файлы и проверьте накопитель средствами производителя."));
                }
            }

            bool smartWarning = false;
            bool smartKnown = false;
            foreach (SmartDeviceStatus device in snapshot.SmartDevices)
            {
                if (device.PredictsFailure == true)
                {
                    smartWarning = true;
                    string deviceName = String.IsNullOrWhiteSpace(device.InstanceName)
                        ? "Накопитель"
                        : device.InstanceName;
                    recommendations.Add(new Recommendation(
                        RecommendationSeverity.Critical,
                        "SMART сообщает о возможном отказе",
                        deviceName + ". Немедленно сохраните важные файлы на другой носитель и проверьте диск утилитой производителя. Не откладывайте резервное копирование."));
                }
                else if (device.PredictsFailure == false)
                {
                    smartKnown = true;
                }
            }

            foreach (LogicalVolumeInfo volume in snapshot.Volumes)
            {
                if (volume.SizeBytes == 0 || volume.FreePercent > 15.0)
                    continue;

                RecommendationSeverity severity = volume.FreePercent <= 5.0
                    ? RecommendationSeverity.Critical
                    : RecommendationSeverity.Warning;
                recommendations.Add(new Recommendation(
                    severity,
                    "Мало свободного места на " + (volume.DeviceId ?? "разделе"),
                    String.Format(System.Globalization.CultureInfo.CurrentCulture,
                        "Свободно {0:N1}% ({1}) из {2}. Освободите место, удаляя только ненужные файлы; можно начать с безопасной очистки временных файлов.",
                        volume.FreePercent, FormatBytes(volume.FreeBytes), FormatBytes(volume.SizeBytes))));
            }

            if (snapshot.MemoryDataKnown && snapshot.TotalMemoryBytes > 0)
            {
                double availablePercent = (double)snapshot.AvailableMemoryBytes * 100.0 / snapshot.TotalMemoryBytes;
                if (availablePercent < 10.0)
                {
                    recommendations.Add(new Recommendation(
                        RecommendationSeverity.Warning,
                        "Оперативная память почти заполнена",
                        String.Format(System.Globalization.CultureInfo.CurrentCulture,
                            "Свободно {0:N1}% ({1}). Проверьте процессы в Диспетчере задач и закройте ненужные приложения. Перед покупкой памяти проверьте требования программ и тип памяти.",
                            availablePercent, FormatBytes(snapshot.AvailableMemoryBytes))));
                }
            }

            if (snapshot.CpuLoadPercent.HasValue && snapshot.CpuLoadPercent.Value >= 85)
            {
                recommendations.Add(new Recommendation(
                    RecommendationSeverity.Warning,
                    "Высокая загрузка процессора во время проверки",
                    "Загрузка составила " + snapshot.CpuLoadPercent.Value + "%. Это краткий снимок, а не длительный тест: проверьте в Диспетчере задач, какой процесс нагружает систему, и повторите проверку после завершения фоновых задач."));
            }

            if (!smartKnown && !smartWarning)
            {
                recommendations.Add(new Recommendation(
                    RecommendationSeverity.Information,
                    "Состояние SMART не получено",
                    "Контроллер или драйвер не передал SMART-статус через стандартный интерфейс Windows. Это не означает ни исправность, ни неисправность диска; проверьте накопитель штатной утилитой его производителя."));
            }
            else if (!smartWarning)
            {
                recommendations.Add(new Recommendation(
                    RecommendationSeverity.Information,
                    "Критических SMART-предупреждений не получено",
                    "В доступных SMART-строках Windows флаг прогнозируемого отказа не установлен. Это не заменяет резервное копирование и не гарантирует отсутствие неисправностей."));
            }

            bool hasProblem = recommendations.Exists(delegate(Recommendation item)
            {
                return item.Severity == RecommendationSeverity.Warning ||
                    item.Severity == RecommendationSeverity.Critical;
            });

            if (!hasProblem)
            {
                recommendations.Add(new Recommendation(
                    RecommendationSeverity.Information,
                    "Срочных проблем по доступным данным не найдено",
                    "При необходимости откройте страницу «Оптимизация»: Windows подберёт подходящее обслуживание для HDD или SSD. Продолжайте регулярно создавать резервные копии и устанавливать обновления."));
            }

            if (recommendations.Count == 0)
            {
                recommendations.Add(new Recommendation(
                    RecommendationSeverity.Information,
                    "Недостаточно данных для оценки",
                    "Некоторые системные службы не ответили. Запустите диагностику повторно; приложение не меняло настройки компьютера."));
            }

            return recommendations;
        }

        private static bool IsConcerningDiskStatus(string status)
        {
            return String.Equals(status, "Pred Fail", StringComparison.OrdinalIgnoreCase) ||
                String.Equals(status, "Error", StringComparison.OrdinalIgnoreCase) ||
                String.Equals(status, "Degraded", StringComparison.OrdinalIgnoreCase) ||
                String.Equals(status, "Lost Comm", StringComparison.OrdinalIgnoreCase) ||
                String.Equals(status, "No Contact", StringComparison.OrdinalIgnoreCase);
        }

        public static string FormatBytes(ulong bytes)
        {
            double value = bytes;
            string[] units = { "Б", "КБ", "МБ", "ГБ", "ТБ" };
            int unit = 0;
            while (value >= 1024.0 && unit < units.Length - 1)
            {
                value /= 1024.0;
                unit++;
            }

            return String.Format(System.Globalization.CultureInfo.CurrentCulture, "{0:N1} {1}", value, units[unit]);
        }
    }
}
