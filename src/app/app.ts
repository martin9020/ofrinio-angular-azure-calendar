import { NgStyle } from '@angular/common';
import { Component, computed, signal } from '@angular/core';

type AvailabilityStatus = 'free' | 'booked' | 'pending';
type AppRoute = 'public' | 'login' | 'owner';
type OwnerModal = 'add' | 'edit' | null;
type SelectionMode = 'add' | 'remove';

interface Photo {
  file: string;
  label: string;
}

interface CalendarDay {
  day: number | null;
  dateKey: string;
  status: AvailabilityStatus;
  isToday: boolean;
}

interface AvailabilityEntry {
  date: string;
  status: AvailabilityStatus;
}

interface AdminAvailabilityEntry {
  date: string;
  status: AvailabilityStatus;
  guestName?: string;
  phone?: string;
  notes?: string;
  updatedAt?: string;
}

interface AdminLoginResponse {
  token: string;
  expiresAt: string;
}

interface AdminSettingsResponse {
  supabaseSyncEnabled: boolean;
}

interface OwnerReservation {
  date: string;
  status: AvailabilityStatus;
  name: string;
  phone: string;
  notes: string;
  signature: string;
}

interface DateRange {
  startDate: string;
  endDate: string;
}

interface RawAvailabilityEntry {
  date: string;
  status?: string;
}

declare global {
  interface Window {
    OFRINIO_API_BASE?: string;
  }
}

const ADMIN_TOKEN_STORAGE_KEY = 'ofrinio-admin-token';

const MONTHS_BG = [
  'Януари',
  'Февруари',
  'Март',
  'Април',
  'Май',
  'Юни',
  'Юли',
  'Август',
  'Септември',
  'Октомври',
  'Ноември',
  'Декември',
];

const WEEKDAYS_BG = ['Пон', 'Вт', 'Ср', 'Чет', 'Пет', 'Съб', 'Нед'];

function readStoredAdminToken(): string {
  try {
    return window.sessionStorage.getItem(ADMIN_TOKEN_STORAGE_KEY) ?? '';
  } catch {
    return '';
  }
}

function readRouteFromHash(): AppRoute {
  const hash = window.location.hash.toLowerCase();
  if (hash.startsWith('#/login')) return 'login';
  if (hash.startsWith('#/owner') || hash.startsWith('#/admin')) return 'owner';
  return 'public';
}

function toDateOrdinal(date: string): number {
  const [year, month, day] = date.split('-').map(Number);
  return Date.UTC(year, month - 1, day) / 86400000;
}

function fromDateOrdinal(ordinal: number): string {
  const date = new Date(ordinal * 86400000);
  return `${date.getUTCFullYear()}-${String(date.getUTCMonth() + 1).padStart(2, '0')}-${String(date.getUTCDate()).padStart(2, '0')}`;
}

const FALLBACK_AVAILABILITY: AvailabilityEntry[] = [
  { date: '2026-06-06', status: 'booked' },
  { date: '2026-06-07', status: 'booked' },
  { date: '2026-06-08', status: 'booked' },
  { date: '2026-06-09', status: 'booked' },
  { date: '2026-06-10', status: 'booked' },
  { date: '2026-06-11', status: 'booked' },
  { date: '2026-06-12', status: 'booked' },
  { date: '2026-06-16', status: 'booked' },
  { date: '2026-06-17', status: 'booked' },
  { date: '2026-06-18', status: 'booked' },
  { date: '2026-06-19', status: 'booked' },
  { date: '2026-06-21', status: 'booked' },
  { date: '2026-06-22', status: 'booked' },
  { date: '2026-06-23', status: 'booked' },
  { date: '2026-06-24', status: 'booked' },
  { date: '2026-06-25', status: 'booked' },
  { date: '2026-06-26', status: 'booked' },
  { date: '2026-06-27', status: 'booked' },
  { date: '2026-06-28', status: 'booked' },
  { date: '2026-06-29', status: 'booked' },
  { date: '2026-07-01', status: 'booked' },
  { date: '2026-07-02', status: 'booked' },
  { date: '2026-07-03', status: 'booked' },
  { date: '2026-07-04', status: 'booked' },
  { date: '2026-07-05', status: 'booked' },
  { date: '2026-07-06', status: 'booked' },
  { date: '2026-07-07', status: 'booked' },
  { date: '2026-07-08', status: 'booked' },
  { date: '2026-07-09', status: 'booked' },
  { date: '2026-07-10', status: 'booked' },
  { date: '2026-07-11', status: 'booked' },
  { date: '2026-07-13', status: 'pending' },
  { date: '2026-07-14', status: 'pending' },
  { date: '2026-07-15', status: 'pending' },
  { date: '2026-07-16', status: 'pending' },
  { date: '2026-07-17', status: 'pending' },
  { date: '2026-07-18', status: 'pending' },
  { date: '2026-07-19', status: 'pending' },
  { date: '2026-07-20', status: 'booked' },
  { date: '2026-07-21', status: 'booked' },
  { date: '2026-07-22', status: 'booked' },
  { date: '2026-07-23', status: 'booked' },
  { date: '2026-07-24', status: 'booked' },
  { date: '2026-07-25', status: 'booked' },
  { date: '2026-07-26', status: 'booked' },
  { date: '2026-07-27', status: 'booked' },
];

@Component({
  selector: 'app-root',
  standalone: true,
  imports: [NgStyle],
  templateUrl: './app.html',
  styleUrl: './app.css',
})
export class App {
  readonly phone = '0888 124 195';
  readonly phoneHref = 'tel:+359888124195';
  readonly mapHref = 'https://maps.app.goo.gl/c3q7VnYSKXxaoDFJ7?g_st=ic';
  readonly weekdays = WEEKDAYS_BG;
  readonly route = signal<AppRoute>(readRouteFromHash());
  readonly selectedPhoto = signal<Photo | null>(null);
  readonly selectedDates = signal('');
  readonly apiBase = signal(window.OFRINIO_API_BASE?.replace(/\/$/, '') ?? '');
  readonly apiStatus = signal(this.apiBase().length
    ? 'Свързване към Azure API...'
    : 'Локален демо календар. Готово за свързване с Azure API.');
  readonly apiConnected = signal(false);
  readonly bookingApiConnected = signal(false);
  readonly apiBadgeLabel = signal('Демо режим: локален календар');
  readonly isAzureConnected = computed(() => this.apiConnected());
  readonly availability = signal(new Map<string, AvailabilityStatus>());
  readonly activeMonth = signal(this.initialMonth());
  readonly requestSent = signal(false);
  readonly adminToken = signal(readStoredAdminToken());
  readonly adminLoggedIn = computed(() => this.adminToken().length > 0);
  readonly adminEntries = signal<AdminAvailabilityEntry[]>([]);
  readonly adminUsername = signal('');
  readonly adminPassword = signal('');
  readonly adminStartDate = signal('');
  readonly adminEndDate = signal('');
  readonly adminStatus = signal<AvailabilityStatus>('booked');
  readonly adminGuestName = signal('');
  readonly adminPhone = signal('');
  readonly adminNotes = signal('');
  readonly adminMessage = signal('');
  readonly adminBusy = signal(false);
  readonly adminSupabaseSyncEnabled = signal(false);
  readonly ownerMonth = signal(this.initialMonth());
  readonly ownerSelectedDates = signal<string[]>([]);
  readonly ownerModal = signal<OwnerModal>(null);
  readonly ownerEditDates = signal<string[]>([]);
  readonly ownerFormName = signal('');
  readonly ownerFormPhone = signal('');
  readonly ownerFormNotes = signal('');
  readonly ownerFormStatus = signal<AvailabilityStatus>('booked');
  readonly adminBookedCount = computed(() => this.adminEntries().filter((entry) => entry.status === 'booked').length);
  readonly adminPendingCount = computed(() => this.adminEntries().filter((entry) => entry.status === 'pending').length);
  readonly ownerReservations = computed(() => this.adminEntries().map((entry) => this.toOwnerReservation(entry)));
  readonly ownerReservationByDate = computed(() => {
    const map = new Map<string, OwnerReservation>();
    for (const reservation of this.ownerReservations()) {
      map.set(reservation.date, reservation);
    }
    return map;
  });
  readonly ownerUniqueReservationCount = computed(() => this.countOwnerReservationGroups(this.ownerReservations()));
  readonly ownerCalendarDays = computed(() => this.buildCalendarDays(this.ownerMonth()));
  readonly availabilityTotals = computed(() => {
    const totals = { free: 0, booked: 0, pending: 0 };
    for (const day of this.calendarDays()) {
      if (day.day !== null) {
        totals[day.status] += 1;
      }
    }
    return totals;
  });

  readonly facts = [
    ['Разстояние', '50 м от плажа'],
    ['Капацитет', 'до 4 гости'],
    ['Площ', '45 кв.м.'],
    ['Престой', 'мин. 6 нощувки'],
  ];

  readonly photos: Photo[] = [
    { file: 'IMG_4420_Original.JPG', label: 'Тераса с маса и столове' },
    { file: 'IMG_4424_Original.JPG', label: 'Спалня' },
    { file: 'IMG_4458_Original.JPG', label: 'Всекидневна с ТВ' },
    { file: 'IMG_4460_Original.JPG', label: 'Кухненски бокс' },
    { file: 'IMG_4462_Original.JPG', label: 'Разтегателни дивани' },
    { file: 'IMG_4436_Original.JPG', label: 'Баня с душ' },
    { file: 'IMG_4463_Original.JPG', label: 'Изход към терасата' },
    { file: 'IMG_4413_Original.JPG', label: 'Подход към апартамента' },
  ];

  readonly amenities = [
    ['Плаж', '4 шезлонга и 2 чадъра за свободно плажуване.'],
    ['Кухня', 'Ел. печка, кафе машина, еър фрайър, хладилник и посуда.'],
    ['Комфорт', 'Климатик, ТВ с български канали, тераса и уютна дневна.'],
    ['Паркиране', 'Пред апартамента или на безплатен паркинг в съседство.'],
  ];

  readonly prices = [
    ['Май', '65€', 'спокоен старт на сезона'],
    ['Юни и Септември', '75€', 'най-добър баланс'],
    ['Юли и Август', '85€', 'активен сезон'],
  ];

  private ownerSelectionDrag: { pointerId: number; mode: SelectionMode; lastDate: string } | null = null;
  private ownerLastSelectionAnchor = '';

  readonly currentMonthTitle = computed(() => {
    const value = this.activeMonth();
    return `${MONTHS_BG[value.getMonth()]} ${value.getFullYear()}`;
  });

  readonly ownerMonthTitle = computed(() => {
    const value = this.ownerMonth();
    return `${MONTHS_BG[value.getMonth()]} ${value.getFullYear()}`;
  });

  readonly calendarDays = computed(() => this.buildCalendarDays(this.activeMonth()));

  constructor() {
    window.addEventListener('hashchange', () => this.handleRouteChange());
    this.seedAvailability(FALLBACK_AVAILABILITY);
    void this.loadAvailabilityFromApi();
    if (this.adminToken()) {
      void this.loadAdminAvailability();
      void this.loadAdminSettings();
    }
    this.handleRouteChange();
  }

  navigateTo(route: AppRoute): void {
    if (route === 'public') {
      window.location.hash = 'top';
      this.route.set('public');
      return;
    }

    window.location.hash = route === 'login' ? '/login' : '/owner';
  }

  private handleRouteChange(): void {
    const nextRoute = readRouteFromHash();
    this.route.set(nextRoute);

    if (nextRoute === 'owner') {
      if (!this.adminToken()) {
        this.navigateTo('login');
        return;
      }

      void this.loadAdminAvailability();
      void this.loadAdminSettings();
    }
  }

  photoUrl(photo: Photo): string {
    return `assets/photos/${photo.file}`;
  }

  heroStyle(): Record<string, string> {
    return {
      'background-image':
        'linear-gradient(90deg, rgba(10, 36, 43, 0.78), rgba(10, 36, 43, 0.30)), url("assets/hero-beach.jpg")',
    };
  }

  openPhoto(photo: Photo): void {
    this.selectedPhoto.set(photo);
  }

  closePhoto(): void {
    this.selectedPhoto.set(null);
  }

  previousMonth(): void {
    const value = this.activeMonth();
    this.activeMonth.set(new Date(value.getFullYear(), value.getMonth() - 1, 1));
  }

  nextMonth(): void {
    const value = this.activeMonth();
    this.activeMonth.set(new Date(value.getFullYear(), value.getMonth() + 1, 1));
  }

  onRequestSubmit(event: Event): void {
    event.preventDefault();
    const form = event.target as HTMLFormElement;
    const formData = new FormData(form);
    const name = (formData.get('name') as string).trim();
    const phone = (formData.get('phone') as string).trim();
    const requestedDates = (formData.get('dates') as string || this.selectedDates()).trim();

    if (!name || !phone || !requestedDates) {
      this.apiStatus.set('Моля, попълнете име, телефон и желани дати.');
      return;
    }

    const apiBase = this.apiBase();
    if (apiBase && this.bookingApiConnected()) {
      this.apiStatus.set('Изпращане на запитване...');
      fetch(`${apiBase}/api/booking-requests`, {
        method: 'POST',
        headers: {
          'Content-Type': 'application/json',
          Accept: 'application/json',
        },
        body: JSON.stringify({ name, phone, requestedDates, message: '' }),
      })
        .then((res) => {
          if (!res.ok) throw new Error();
          this.requestSent.set(true);
          this.apiStatus.set('Запитването е изпратено успешно.');
          form.reset();
          this.selectedDates.set('');
        })
        .catch(() => {
          this.apiStatus.set('Възникна грешка при изпращането.');
        });
    } else if (apiBase && !this.bookingApiConnected()) {
      this.apiStatus.set(`Формата за запитване не е свързана в момента. Моля, обадете се на ${this.phone}.`);
    } else {
      this.requestSent.set(true);
      this.apiStatus.set('Демо режим: заявката ще се запази само локално.');
      form.reset();
      this.selectedDates.set('');
    }
  }

  async onAdminLogin(event: Event): Promise<void> {
    event.preventDefault();

    if (!this.apiBase()) {
      this.adminMessage.set('Azure API адресът не е конфигуриран.');
      return;
    }

    if (!this.adminUsername().trim() || !this.adminPassword().trim()) {
      this.adminMessage.set('Въведете потребител и парола.');
      return;
    }

    this.adminBusy.set(true);
    this.adminMessage.set('Проверка на достъпа...');

    try {
      const response = await fetch(`${this.apiBase()}/api/owner/login`, {
        method: 'POST',
        headers: {
          'Content-Type': 'application/json',
          Accept: 'application/json',
        },
        body: JSON.stringify({
          username: this.adminUsername(),
          password: this.adminPassword(),
        }),
      });

      if (!response.ok) {
        throw new Error(response.status === 503
          ? 'Админ паролата не е настроена в Azure.'
          : 'Грешна парола или недостъпно Azure API.');
      }

      const login = (await response.json()) as AdminLoginResponse;
      this.adminToken.set(login.token);
      this.adminPassword.set('');
      window.sessionStorage.setItem(ADMIN_TOKEN_STORAGE_KEY, login.token);
      this.adminMessage.set('Влязохте в Azure календара.');
      await this.loadAdminAvailability();
      await this.loadAdminSettings();
      this.navigateTo('owner');
    } catch (error) {
      this.adminMessage.set(error instanceof Error ? error.message : 'Неуспешен вход.');
    } finally {
      this.adminBusy.set(false);
    }
  }

  adminLogout(): void {
    this.adminToken.set('');
    this.adminEntries.set([]);
    this.adminSupabaseSyncEnabled.set(false);
    this.adminMessage.set('Излязохте от админ режима.');
    window.sessionStorage.removeItem(ADMIN_TOKEN_STORAGE_KEY);
    this.navigateTo('login');
  }

  async onAdminSaveRange(event: Event): Promise<void> {
    event.preventDefault();

    if (!this.adminToken()) {
      this.adminMessage.set('Влезте, за да редактирате Azure календара.');
      return;
    }

    const startDate = this.adminStartDate().trim();
    const endDate = (this.adminEndDate().trim() || startDate);

    if (!startDate || !endDate) {
      this.adminMessage.set('Изберете начална и крайна дата.');
      return;
    }

    this.adminBusy.set(true);
    this.adminMessage.set('Записване в Azure SQL...');

    try {
      const response = await fetch(`${this.apiBase()}/api/owner/availability/range`, {
        method: 'PUT',
        headers: this.adminJsonHeaders(),
        body: JSON.stringify({
          startDate,
          endDate,
          status: this.adminStatus(),
          guestName: this.adminGuestName(),
          phone: this.adminPhone(),
          notes: this.adminNotes(),
        }),
      });

      await this.handleAdminResponse(response);
      this.adminMessage.set('Датите са записани в Azure SQL.');
      await this.loadAdminAvailability();
      await this.loadAvailabilityFromApi();
    } catch (error) {
      this.adminMessage.set(error instanceof Error ? error.message : 'Грешка при запис.');
    } finally {
      this.adminBusy.set(false);
    }
  }

  async importSupabaseToAzure(): Promise<void> {
    if (!this.adminToken()) {
      this.adminMessage.set('Влезте, за да импортирате от Supabase.');
      return;
    }

    this.adminBusy.set(true);
    this.adminMessage.set('Копиране от Supabase към Azure SQL...');

    try {
      const response = await fetch(`${this.apiBase()}/api/owner/import-supabase`, {
        method: 'POST',
        headers: this.adminJsonHeaders(),
        body: JSON.stringify({ replaceExisting: false }),
      });
      const body = await this.handleAdminResponse<{ imported: number; source: string }>(response);
      this.adminMessage.set(`Импортирани са ${body.imported} дати от ${body.source}.`);
      await this.loadAdminAvailability();
      await this.loadAvailabilityFromApi();
    } catch (error) {
      this.adminMessage.set(error instanceof Error ? error.message : 'Грешка при импорт.');
    } finally {
      this.adminBusy.set(false);
    }
  }

  async loadAdminSettings(): Promise<void> {
    if (!this.adminToken() || !this.apiBase()) {
      return;
    }

    try {
      const response = await fetch(`${this.apiBase()}/api/owner/settings`, {
        headers: this.adminJsonHeaders(),
      });
      const settings = await this.handleAdminResponse<AdminSettingsResponse>(response);
      this.adminSupabaseSyncEnabled.set(settings.supabaseSyncEnabled);
    } catch (error) {
      if (error instanceof Error && error.message.includes('401')) {
        this.adminLogout();
        return;
      }
      this.adminMessage.set(error instanceof Error ? error.message : 'Could not load admin settings.');
    }
  }

  async onAdminSupabaseSyncChange(event: Event): Promise<void> {
    if (!this.adminToken()) {
      this.adminMessage.set('Log in before changing Supabase sync.');
      return;
    }

    const checkbox = event.target as HTMLInputElement;
    const previous = this.adminSupabaseSyncEnabled();
    const next = checkbox.checked;
    this.adminSupabaseSyncEnabled.set(next);
    this.adminBusy.set(true);
    this.adminMessage.set('Saving sync setting...');

    try {
      const response = await fetch(`${this.apiBase()}/api/owner/settings`, {
        method: 'PUT',
        headers: this.adminJsonHeaders(),
        body: JSON.stringify({ supabaseSyncEnabled: next }),
      });
      const settings = await this.handleAdminResponse<AdminSettingsResponse>(response);
      this.adminSupabaseSyncEnabled.set(settings.supabaseSyncEnabled);
      this.adminMessage.set(settings.supabaseSyncEnabled
        ? 'Supabase sync is enabled.'
        : 'Supabase sync is disabled.');
    } catch (error) {
      this.adminSupabaseSyncEnabled.set(previous);
      checkbox.checked = previous;
      this.adminMessage.set(error instanceof Error ? error.message : 'Could not save sync setting.');
    } finally {
      this.adminBusy.set(false);
    }
  }

  ownerPreviousMonth(): void {
    this.ownerLastSelectionAnchor = '';
    const value = this.ownerMonth();
    this.ownerMonth.set(new Date(value.getFullYear(), value.getMonth() - 1, 1));
  }

  ownerNextMonth(): void {
    this.ownerLastSelectionAnchor = '';
    const value = this.ownerMonth();
    this.ownerMonth.set(new Date(value.getFullYear(), value.getMonth() + 1, 1));
  }

  ownerReservationFor(date: string): OwnerReservation | undefined {
    return this.ownerReservationByDate().get(date);
  }

  isOwnerDateSelected(date: string): boolean {
    return this.ownerSelectedDates().includes(date);
  }

  ownerStatusClass(status: AvailabilityStatus): string {
    return status;
  }

  ownerStatusLabel(status: AvailabilityStatus): string {
    return this.statusLabel(status);
  }

  ownerFormatDate(date: string): string {
    if (!date) return '';
    const [year, month, day] = date.split('-').map(Number);
    return `${day} ${MONTHS_BG[month - 1]} ${year}`;
  }

  ownerFormatDateList(dates: string[]): string {
    const sorted = this.sortDates(dates);
    if (sorted.length <= 3) {
      return sorted.map((date) => this.ownerFormatDate(date)).join(', ');
    }

    return `${sorted.slice(0, 3).map((date) => this.ownerFormatDate(date)).join(', ')} + ${sorted.length - 3}`;
  }

  beginOwnerDateSelection(event: PointerEvent, day: CalendarDay): void {
    if (!day.day || event.button !== 0) {
      return;
    }

    event.preventDefault();
    const mode: SelectionMode = this.isOwnerDateSelected(day.dateKey) ? 'remove' : 'add';
    this.ownerSelectionDrag = { pointerId: event.pointerId, mode, lastDate: day.dateKey };
    (event.currentTarget as HTMLElement).setPointerCapture?.(event.pointerId);
    this.applyOwnerSelection([day.dateKey], mode);
    this.ownerLastSelectionAnchor = day.dateKey;
  }

  continueOwnerDateSelection(event: PointerEvent): void {
    const drag = this.ownerSelectionDrag;
    if (!drag || drag.pointerId !== event.pointerId) {
      return;
    }

    const element = document.elementFromPoint(event.clientX, event.clientY);
    const dateElement = element?.closest?.('[data-owner-date]') as HTMLElement | null;
    const date = dateElement?.dataset['ownerDate'];
    if (!date || date === drag.lastDate) {
      return;
    }

    this.applyOwnerSelection(this.dateRangeStrings(drag.lastDate, date), drag.mode);
    drag.lastDate = date;
    this.ownerLastSelectionAnchor = date;
  }

  endOwnerDateSelection(event: PointerEvent): void {
    const drag = this.ownerSelectionDrag;
    if (!drag || drag.pointerId !== event.pointerId) {
      return;
    }

    (event.currentTarget as HTMLElement).releasePointerCapture?.(event.pointerId);
    this.ownerSelectionDrag = null;
  }

  toggleOwnerDateFromKeyboard(event: KeyboardEvent, day: CalendarDay): void {
    if (!day.day || (event.key !== 'Enter' && event.key !== ' ')) {
      return;
    }

    event.preventDefault();
    this.applyOwnerSelection(
      event.shiftKey && this.ownerLastSelectionAnchor
        ? this.dateRangeStrings(this.ownerLastSelectionAnchor, day.dateKey)
        : [day.dateKey],
      this.isOwnerDateSelected(day.dateKey) ? 'remove' : 'add');
    this.ownerLastSelectionAnchor = day.dateKey;
  }

  clearOwnerSelection(): void {
    this.ownerSelectedDates.set([]);
    this.ownerLastSelectionAnchor = '';
  }

  openOwnerAdd(date?: string): void {
    const dates = this.sortDates(date ? [date] : this.ownerSelectedDates());
    const fallback = this.todayDateKey();
    this.ownerEditDates.set(dates.length ? dates : [fallback]);
    this.ownerSelectedDates.set(dates.length ? dates : [fallback]);
    this.ownerFormName.set('');
    this.ownerFormPhone.set('');
    this.ownerFormNotes.set('');
    this.ownerFormStatus.set('booked');
    this.ownerModal.set('add');
  }

  openOwnerEdit(reservation: OwnerReservation): void {
    const dates = this.getOwnerReservationBatch(reservation);
    this.ownerEditDates.set(dates);
    this.ownerSelectedDates.set(dates);
    this.ownerFormName.set(reservation.name);
    this.ownerFormPhone.set(reservation.phone);
    this.ownerFormNotes.set(reservation.notes);
    this.ownerFormStatus.set(reservation.status);
    this.ownerModal.set('edit');
  }

  closeOwnerModal(): void {
    this.ownerModal.set(null);
  }

  async saveOwnerReservation(): Promise<void> {
    const dates = this.ownerEditDates();
    if (!dates.length) {
      this.adminMessage.set('Select at least one date.');
      return;
    }

    if (this.ownerFormStatus() !== 'free' && !this.ownerFormName().trim()) {
      this.adminMessage.set('Enter a guest name.');
      return;
    }

    await this.saveOwnerDateRanges(
      dates,
      this.ownerFormStatus(),
      this.ownerFormName(),
      this.ownerFormPhone(),
      this.ownerFormNotes());
    this.ownerModal.set(null);
    this.clearOwnerSelection();
  }

  async deleteOwnerReservation(reservation: OwnerReservation): Promise<void> {
    await this.saveOwnerDateRanges(
      this.getOwnerReservationBatch(reservation),
      'free',
      '',
      '',
      '');
    this.clearOwnerSelection();
  }

  async deleteOwnerSelectedReservations(): Promise<void> {
    const dates = this.ownerSelectedDates()
      .filter((date) => this.ownerReservationByDate().has(date));

    if (!dates.length) {
      this.adminMessage.set('No selected reservations to delete.');
      return;
    }

    await this.saveOwnerDateRanges(dates, 'free', '', '', '');
    this.clearOwnerSelection();
  }

  async loadAdminAvailability(): Promise<void> {
    if (!this.adminToken() || !this.apiBase()) {
      return;
    }

    try {
      const response = await fetch(`${this.apiBase()}/api/owner/availability`, {
        headers: this.adminJsonHeaders(),
      });
      const rows = await this.handleAdminResponse<AdminAvailabilityEntry[]>(response);
      this.adminEntries.set(rows.map((entry) => ({
        ...entry,
        status: this.normalizeAvailabilityStatus(entry.status),
      })));
    } catch (error) {
      if (error instanceof Error && error.message.includes('401')) {
        this.adminLogout();
      }
      this.adminMessage.set(error instanceof Error ? error.message : 'Грешка при зареждане.');
    }
  }

  statusLabel(status: AvailabilityStatus): string {
    if (status === 'booked') return 'Заето';
    if (status === 'pending') return 'Запитване';
    return 'Свободно';
  }

  selectDay(day: CalendarDay): void {
    if (day.day === null) {
      return;
    }

    this.selectedDates.set(day.dateKey);
    this.apiStatus.set(`Избрана дата: ${day.dateKey}. Попълнете желания период във формата.`);
  }

  private async saveOwnerDateRanges(
    dates: string[],
    status: AvailabilityStatus,
    guestName: string,
    phone: string,
    notes: string): Promise<void> {
    if (!this.adminToken()) {
      this.navigateTo('login');
      return;
    }

    const ranges = this.groupDateRanges(dates);
    if (!ranges.length) {
      return;
    }

    this.adminBusy.set(true);
    this.adminMessage.set('Saving to Azure SQL...');

    try {
      for (const range of ranges) {
        const response = await fetch(`${this.apiBase()}/api/owner/availability/range`, {
          method: 'PUT',
          headers: this.adminJsonHeaders(),
          body: JSON.stringify({
            startDate: range.startDate,
            endDate: range.endDate,
            status,
            guestName,
            phone,
            notes,
          }),
        });
        await this.handleAdminResponse(response);
      }

      this.adminMessage.set(status === 'free'
        ? 'Selected dates were cleared.'
        : 'Reservation dates were saved in Azure SQL.');
      await this.loadAdminAvailability();
      await this.loadAvailabilityFromApi();
    } catch (error) {
      this.adminMessage.set(error instanceof Error ? error.message : 'Could not save reservation.');
    } finally {
      this.adminBusy.set(false);
    }
  }

  private applyOwnerSelection(dates: string[], mode: SelectionMode): void {
    const next = new Set(this.ownerSelectedDates());
    for (const date of dates) {
      if (mode === 'remove') {
        next.delete(date);
      } else {
        next.add(date);
      }
    }

    this.ownerSelectedDates.set(this.sortDates([...next]));
  }

  private dateRangeStrings(startDate: string, endDate: string): string[] {
    const start = Math.min(toDateOrdinal(startDate), toDateOrdinal(endDate));
    const end = Math.max(toDateOrdinal(startDate), toDateOrdinal(endDate));
    const dates: string[] = [];

    for (let ordinal = start; ordinal <= end; ordinal += 1) {
      dates.push(fromDateOrdinal(ordinal));
    }

    return dates;
  }

  private groupDateRanges(dates: string[]): DateRange[] {
    const sorted = this.sortDates(dates);
    if (!sorted.length) {
      return [];
    }

    const ranges: DateRange[] = [];
    let startDate = sorted[0];
    let previousDate = sorted[0];

    for (const date of sorted.slice(1)) {
      if (toDateOrdinal(date) - toDateOrdinal(previousDate) > 1) {
        ranges.push({ startDate, endDate: previousDate });
        startDate = date;
      }

      previousDate = date;
    }

    ranges.push({ startDate, endDate: previousDate });
    return ranges;
  }

  private getOwnerReservationBatch(target: OwnerReservation): string[] {
    const reservations = this.ownerReservations()
      .filter((reservation) => reservation.signature === target.signature)
      .sort((a, b) => a.date.localeCompare(b.date));
    const targetIndex = reservations.findIndex((reservation) => reservation.date === target.date);

    if (targetIndex === -1) {
      return [target.date];
    }

    let start = targetIndex;
    let end = targetIndex;

    while (start > 0 &&
      toDateOrdinal(reservations[start].date) - toDateOrdinal(reservations[start - 1].date) <= 1) {
      start -= 1;
    }

    while (end < reservations.length - 1 &&
      toDateOrdinal(reservations[end + 1].date) - toDateOrdinal(reservations[end].date) <= 1) {
      end += 1;
    }

    return reservations.slice(start, end + 1).map((reservation) => reservation.date);
  }

  private toOwnerReservation(entry: AdminAvailabilityEntry): OwnerReservation {
    const name = entry.guestName?.trim() || 'Reserved';
    const phone = entry.phone?.trim() || '';
    const notes = entry.notes?.trim() || '';
    const status = this.normalizeAvailabilityStatus(entry.status);
    const signature = [
      name.toLowerCase(),
      phone.replace(/\D/g, ''),
      notes.toLowerCase(),
      status,
    ].join('|');

    return {
      date: entry.date,
      status,
      name,
      phone,
      notes,
      signature,
    };
  }

  private countOwnerReservationGroups(reservations: OwnerReservation[]): number {
    const sorted = [...reservations].sort((a, b) =>
      a.signature.localeCompare(b.signature) || a.date.localeCompare(b.date));
    let count = 0;
    let previousSignature = '';
    let previousOrdinal: number | null = null;

    for (const reservation of sorted) {
      const ordinal = toDateOrdinal(reservation.date);
      if (reservation.signature !== previousSignature ||
        previousOrdinal === null ||
        ordinal - previousOrdinal > 1) {
        count += 1;
      }

      previousSignature = reservation.signature;
      previousOrdinal = ordinal;
    }

    return count;
  }

  private sortDates(dates: string[]): string[] {
    return [...new Set(dates)].sort((a, b) => a.localeCompare(b));
  }

  private todayDateKey(): string {
    const today = new Date();
    return `${today.getFullYear()}-${String(today.getMonth() + 1).padStart(2, '0')}-${String(today.getDate()).padStart(2, '0')}`;
  }

  private initialMonth(): Date {
    const today = new Date();
    return new Date(today.getFullYear(), today.getMonth(), 1);
  }

  private seedAvailability(entries: AvailabilityEntry[]): void {
    const map = new Map<string, AvailabilityStatus>();
    for (const entry of entries) {
      map.set(entry.date, entry.status);
    }
    this.availability.set(map);
  }

  private async loadAvailabilityFromApi(): Promise<void> {
    if (await this.tryLoadAvailabilityFromAzure()) {
      return;
    }

    this.apiConnected.set(false);
    this.bookingApiConnected.set(false);
    this.apiBadgeLabel.set('Демо режим: локален календар');
    this.apiStatus.set('Azure API не е достъпно. Показва се локален демо календар.');
  }

  private async tryLoadAvailabilityFromAzure(): Promise<boolean> {
    const apiBase = this.apiBase();
    if (!apiBase) return false;

    try {
      const response = await fetch(`${apiBase}/api/availability`, {
        headers: { Accept: 'application/json' },
      });
      if (!response.ok) throw new Error(`HTTP ${response.status}`);
      const rows = (await response.json()) as RawAvailabilityEntry[];
      this.seedAvailability(this.normalizeAvailabilityEntries(rows));
      this.apiConnected.set(true);
      this.bookingApiConnected.set(true);
      this.apiBadgeLabel.set('Azure API свързан');
      this.apiStatus.set('Календарът е зареден от Azure API.');
      return true;
    } catch {
      this.bookingApiConnected.set(false);
      return false;
    }
  }

  private adminJsonHeaders(): Record<string, string> {
    return {
      'Content-Type': 'application/json',
      Accept: 'application/json',
      Authorization: `Bearer ${this.adminToken()}`,
    };
  }

  private async handleAdminResponse<T = unknown>(response: Response): Promise<T> {
    if (response.status === 401) {
      throw new Error('401: Няма достъп или сесията е изтекла.');
    }

    if (!response.ok) {
      let message = `Azure API върна HTTP ${response.status}.`;
      try {
        const body = await response.json() as { error?: string; title?: string; detail?: string };
        message = body.error ?? body.detail ?? body.title ?? message;
      } catch {
        // Keep the HTTP status message.
      }
      throw new Error(message);
    }

    return await response.json() as T;
  }

  private normalizeAvailabilityEntries(rows: RawAvailabilityEntry[]): AvailabilityEntry[] {
    const statusWeight: Record<AvailabilityStatus, number> = {
      free: 0,
      pending: 1,
      booked: 2,
    };
    const byDate = new Map<string, AvailabilityStatus>();

    for (const row of rows) {
      if (!/^\d{4}-\d{2}-\d{2}/.test(row.date)) continue;
      const date = row.date.slice(0, 10);
      const status = this.normalizeAvailabilityStatus(row.status);
      const current = byDate.get(date) ?? 'free';

      if (statusWeight[status] >= statusWeight[current]) {
        byDate.set(date, status);
      }
    }

    return Array.from(byDate, ([date, status]) => ({ date, status }));
  }

  private normalizeAvailabilityStatus(value: string | undefined): AvailabilityStatus {
    switch ((value ?? '').trim().toLowerCase()) {
      case 'booked':
      case 'reserved':
      case 'confirmed':
      case 'потвърдена':
        return 'booked';
      case 'pending':
      case 'request':
      case 'чакаща':
        return 'pending';
      default:
        return 'free';
    }
  }

  private buildCalendarDays(monthDate: Date): CalendarDay[] {
    const year = monthDate.getFullYear();
    const month = monthDate.getMonth();
    const firstDay = (new Date(year, month, 1).getDay() + 6) % 7;
    const daysInMonth = new Date(year, month + 1, 0).getDate();
    const today = new Date();
    const output: CalendarDay[] = [];

    for (let index = 0; index < firstDay; index += 1) {
      output.push({ day: null, dateKey: `empty-${index}`, status: 'free', isToday: false });
    }

    for (let day = 1; day <= daysInMonth; day += 1) {
      const dateKey = `${year}-${String(month + 1).padStart(2, '0')}-${String(day).padStart(2, '0')}`;
      output.push({
        day,
        dateKey,
        status: this.availability().get(dateKey) ?? 'free',
        isToday:
          today.getFullYear() === year && today.getMonth() === month && today.getDate() === day,
      });
    }

    return output;
  }
}
