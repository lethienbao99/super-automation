# SuperAutomation

Tool chạy trên máy cá nhân, tự động hoá việc làm task AMIS cho HRM9 bằng Claude:

1. Lấy công việc của bạn từ AMIS Công việc, hiển thị thành bảng Kanban.
2. AI đọc task (kèm task cha, bình luận, tệp đính kèm) và đánh giá yêu cầu đã đủ rõ chưa.
3. AI code trên một nhánh riêng, tự review, build và test.
4. Bạn xem diff rồi duyệt, commit và push.

Tool **không tự push**: chỉ push khi bạn bấm.

---

## 1. Cài đặt

### Yêu cầu trên máy

| Phần mềm | Dùng để |
|---|---|
| [.NET 8 SDK](https://dotnet.microsoft.com/download/dotnet/8.0) | Chạy tool |
| [Node.js](https://nodejs.org) 18 trở lên | Chế độ "API key" và "API key qua Agent SDK" |
| Git | Tạo nhánh và commit. Phải có quyền push lên repo HRM9 |
| Workspace `hrm-wiki` | Gồm `src\HRM9` (backend), có thể kèm `src\Vnr.Dev.HrmPortal` (frontend), `CLAUDE.md`, wiki và `tools\` (db, e2e, claude-hooks) |
| Visual Studio / MSBuild, IIS | Chỉ cần nếu muốn tool build và test giao diện trên web đang chạy |

Cần thêm **một** trong hai thứ sau:
- **API key Anthropic**: lấy ở [console.anthropic.com](https://console.anthropic.com) › API keys. Không cần cài hay đăng nhập Claude Code.
- **Claude Code đã đăng nhập** bằng tài khoản Pro hoặc Max: `npm i -g @anthropic-ai/claude-code`, rồi chạy `claude` và đăng nhập.

### Lấy tool về và chạy

```powershell
cd D:\VibeCode\Tool\SuperAutomation        # hoặc thư mục bạn chép tool vào
cd sdk-runner; npm install; cd ..          # một lần — cài Claude Agent SDK
dotnet run --no-launch-profile
```

Mở **http://localhost:5080**. Muốn dừng tool thì nhấn Ctrl+C trong cửa sổ đang chạy.

> Nếu cổng 5080 đã có ứng dụng khác dùng, sửa `Urls` trong `appsettings.json`.

---

## 2. Cấu hình lần đầu

Bấm **⚙ Cấu hình** trên thanh trên cùng. Mọi thay đổi được lưu vào `appsettings.Local.json` trên máy này và áp dụng ngay, không cần khởi động lại tool.

Làm theo thứ tự:

### Bước 1. AMIS: nhập session
Bấm **Đổi session AMIS**, rồi dán giá trị `x-sessionid`:
1. Mở AMIS Công việc trên trình duyệt và nhấn F12.
2. Chọn tab Network, bấm một request bất kỳ tới `TaskAPI`.
3. Trong Request Headers, sao chép giá trị `x-sessionid`.

Khi session hết hạn, cột AMIS sẽ báo lỗi. Làm lại các bước trên là xong.

### Bước 2. Source code
- **Thư mục workspace**: ví dụ `D:\Source\hrm-wiki`. Claude chạy trong thư mục này nên đọc được `CLAUDE.md` và wiki.
- Bấm **Quét repo**, rồi chọn:
  - **Repo chính**: backend `src\HRM9`.
  - **Repo phụ** (nếu AI cần sửa cả frontend): ví dụ `src\Vnr.Dev.HrmPortal`.
- **File cấu hình local**: các file môi trường luôn sửa trên máy, như `Web.config` hay `config.json`. Bấm "Lấy các file đang sửa" để điền nhanh. Các file này:
  - không bị tính là "chưa commit";
  - không bao giờ được commit vào nhánh AI;
  - AI được dặn không sửa.

### Bước 3. Claude: chọn cách xác thực

| Cách | Khi nào dùng | Tiền |
|---|---|---|
| **Tài khoản đăng nhập Claude Code** | Máy đã chạy `claude` và đăng nhập Pro/Max | Tính vào gói. Chi phí hiện trong tool chỉ là **ước tính** |
| **API key Anthropic** | Có key với tiền nạp thật | Trừ vào tài khoản API |
| **API key qua Agent SDK** | Key chỉ có **credit khuyến mãi** (loại áp dụng cho "Agent SDK, API" nhưng không cho Claude Code). Cũng dùng được cho key có tiền nạp thật | Trừ vào tài khoản API |

Ba cách cho chất lượng như nhau: cùng model, cùng skill, cùng quy trình. Khác nhau chỉ ở chỗ ai trả tiền.

- **Nhiều key:** bấm **+ Thêm key** cho từng key, rồi chọn cách xoay tua:
  - *Dùng lần lượt*: dùng key trên cùng, key đó lỗi mới chuyển sang key sau.
  - *Xoay vòng*: chia đều các phiên cho các key.

  Key lỗi được cho nghỉ tạm thời, rồi tool tự chuyển key khác: hết tiền nghỉ 6 giờ, sai key nghỉ 24 giờ, chạm rate limit nghỉ 2 phút.
- **Model:** chọn trong danh sách. Opus cho chất lượng tốt nhất, Sonnet rẻ hơn nhiều. Khung chat có ô chọn model riêng.
- **Giới hạn chi phí (USD, 0 = không giới hạn):** mỗi job (từ phân tích tới test) và mỗi tin nhắn chat. Khi chạm trần, AI dừng nhưng phần đã làm vẫn giữ trên nhánh AI.
- Bấm **Kiểm tra kết nối** hoặc **Kiểm tra tất cả key**. Mỗi lần gọi thật Claude một lần, tốn khoảng $0,1.

### Bước 4. Skill: cài bộ skill HRM
Tab **Skill** → bấm **Cài mục còn thiếu**.

- Bước code và review của tool gọi skill `ship-check`, `soat-slop`, `hieu-nang-truy-van` và agent `vnr-reviewer`. Thiếu các mục này thì AI không có checklist để làm theo, và chất lượng giảm rõ rệt.
- Bộ cài nằm trong thư mục [claude-kit/](claude-kit/) của tool. Ô "Cài vào" có hai lựa chọn:
  - *Máy này* (`%USERPROFILE%\.claude`, khuyên dùng).
  - *Workspace* (`hrm-wiki\.claude`, đi theo repo).
- Máy chưa từng đăng nhập Claude cũng cài được: tool tự tạo thư mục `.claude`. Claude đọc skill từ thư mục này, không cần đăng nhập.
- Nếu bạn sửa skill trên máy mình, bấm **Lấy bản máy → bộ cài** để cập nhật bộ cài trước khi gửi tool cho người khác.

### Bước 5. Quy trình
- **Tự chuyển sang code khi yêu cầu đã rõ:** tắt thì task dừng ở cột "Đã rõ yêu cầu" chờ bạn bấm.
- **Chờ tôi xác nhận trước khi commit** (mặc định bật): khi xong, code AI nằm chưa commit trên nhánh AI để bạn kiểm tay trên web. Bạn bấm "Tạo commit & push" để commit. Trong lúc đó repo bị giữ, job khác chưa code được.
- **Cho chat tạo file:** dùng khi cần AI xuất biên bản test dạng docx hoặc xlsx. Chỉ bật khi cần.

### Bước 6. Môi trường test và DB test (tuỳ chọn)
- **Môi trường test:**
  - URL web Main, tài khoản test (cho Playwright), web khác như Portal `http://localhost:4200`.
  - Bật hoặc tắt việc build trước khi test, và build lại trên nhánh của bạn sau khi AI xong.
- **DB test:**
  - DB mà AI được **đọc**. Khi lưu, tool cập nhật `tools\db\ro.local.json`.
  - Tuỳ chọn cho tool **tạo dữ liệu test thật** (chỉ INSERT, có dấu `AI-TEST-<task>`, chạy trong transaction, tự dọn sau khi test).

### Bước 7. Tự động (tuỳ chọn, mặc định tắt)
- Làm mới danh sách task theo chu kỳ.
- Tự giao task mới cho AI phân tích. Mỗi task tốn khoảng $0,5–2. Có thể giới hạn theo dự án hoặc theo nhánh.

---

## 3. Sử dụng

### Bảng Kanban

| Cột | Ý nghĩa |
|---|---|
| **Công việc AMIS** | Task của bạn theo hạn: đến hạn hôm nay, sắp đến hạn, quá hạn. Tìm được theo tên, ID hoặc dự án, và lọc theo dự án |
| **Phân tích yêu cầu** | Kéo task vào đây. AI đọc task (chỉ đọc, không đụng repo) |
| **Cần bổ sung** | Yêu cầu chưa đủ. Câu hỏi của AI nằm trong tab Chat |
| **Đã rõ yêu cầu** | Xem bản phân tích, rồi bấm **Bắt đầu code** |
| **AI đang code** | Lập kế hoạch, code, chạy ship-check, sửa theo review |
| **Review & Test** | vnr-reviewer, soát slop, build, Playwright, truy vấn DB |
| **Chờ bạn** | AI cần bạn trả lời, hoặc đã xong và chờ duyệt |
| Kết thúc | Đã duyệt, từ chối, lỗi hoặc huỷ |

### Làm một task
1. Bấm một task AMIS, hoặc kéo nó sang cột **Phân tích yêu cầu**.
   - Popup hiện mô tả, task cha và tệp đính kèm.
   - Bạn có thể upload thêm tài liệu và ghi chú cho AI.
   - Thẻ có nhãn **⚠ Khác nhánh** khi dự án của task không khớp nhánh repo đang mở. Đổi nhánh ngay trong cảnh báo, hoặc ghi nhớ từ khoá nhánh cho dự án.
2. AI phân tích và ghi `analysis.md`: hiểu yêu cầu, phạm vi và file dự kiến, tiêu chí chấp nhận, giả định, điểm cần bổ sung.
   - Thiếu thông tin: trả lời trong **Chat**, rồi bấm "Gửi & phân tích lại".
   - Bạn cũng có thể bấm "Vẫn giao code" để AI theo phương án nó đề xuất.
3. Bấm **Bắt đầu code**. Repo phải sạch (file cấu hình local không tính).
   - Tool tạo nhánh `ai/amis-<taskId>-<thời gian>` từ nhánh hiện tại, ở mọi repo đã chọn.
   - AI code và chạy `ship-check`.
   - Một phiên AI riêng review bằng `vnr-reviewer` và `soat-slop`. Nếu review kết luận FAIL, AI sửa rồi review lại.
   - Tool build, AI viết và chạy Playwright, rồi kiểm dữ liệu.
4. Nếu AI chưa hiểu một điểm, nó dừng và hỏi trong Chat. Trả lời rồi bấm **Gửi & tiếp tục**: AI làm tiếp đúng phiên cũ, không làm lại từ đầu.
5. Khi xong, xem các tab:
   - **Diff**.
   - **Tài liệu**: phân tích, kế hoạch, review, test, ảnh bằng chứng, mô tả PR.
   - **Nhật ký**.
6. Duyệt:
   - **Đang giữ code chưa commit** (bật "Chờ xác nhận"): bấm **Tạo commit & push…**, chọn file, sửa message, tick Push nếu muốn.
   - **Đã commit sẵn**: bấm **Duyệt & push**, **Chỉ duyệt** hoặc **Từ chối**.

### Muốn sửa thêm sau khi job xong
Bấm **Yêu cầu AI sửa…** và mô tả cần sửa gì, hoặc để trống để AI làm theo những gì đã chốt trong Chat. AI sửa tiếp trên nhánh AI (không code lại), rồi tool chạy lại review và test.

**Review + test lại** chạy lại hai bước đó mà không sửa code.

### Commit message và PR
Theo chuẩn của công ty:

```
[ID TASK] - Mô tả            ← dòng đầu, lấy từ task cha (tiêu đề PR)

Mục đích:
...
Thay đổi:
...
```

- Phần thân lấy từ `pr.md` do bước review viết. TFS dùng nó làm mô tả PR mặc định.
- Bạn sửa được message trong hộp commit.
- Tiêu đề PR có nút Copy ở chi tiết job.

### Chạy hàng loạt
1. Ở cột AMIS, tick ô trên từng thẻ task, hoặc bấm "☐ Chọn N task đang hiển thị". Tối đa 30 task.
2. Bấm **Chạy hàng loạt…** rồi chọn cách xử lý khi hai task sửa cùng một file:
   - **Nối nhánh** (khuyên dùng): task sau tạo nhánh từ nhánh của task trước, nên không conflict. Thẻ có chip `↳ nối #X`. Khi merge, PR của #X phải vào trước.
   - **Tách nhánh, chỉ cảnh báo:** các nhánh độc lập. Thẻ có chip `⚠ Trùng file với #Y`.
3. Tool chạy như sau:
   - Phân tích 2 task song song.
   - Task đủ rõ thì tự sang code. Task chưa rõ dừng ở "Cần bổ sung" chờ bạn.
   - Code, review và test lần lượt **từng task**, mỗi task một nhánh.
   - Xong mỗi task, tool **tự commit vào nhánh AI (không push)** rồi trả repo cho task sau.
4. Bạn duyệt và push từng task ở cột "Chờ bạn".

Trước khi chạy lô: không được có job nào đang giữ repo, và repo nên sạch.

### Chat theo task
Mỗi task có một khung chat riêng.

- **AI đọc được:** task, source code, wiki và kết quả lần chạy gần nhất.
- **Chỉ đọc:** chat không sửa code.
- **Đính kèm:** nút 📎, dán ảnh bằng Ctrl+V, hoặc kéo thả tệp.
- **Dùng cho các bước sau:** các quyết định chốt trong chat được ghi vào `chat.md`. Mọi bước pipeline đều đọc file này.
- **Phiên mới:** AI quên ngữ cảnh cũ. Lịch sử vẫn hiển thị.

### Theo dõi chi phí
Bấm **📊 Chi phí** trên thanh trên cùng.
- **Lọc:** hôm nay, 7 ngày, 30 ngày, tất cả; tiền thật hoặc ước tính.
- **Xem:** chi phí theo ngày, theo task (bấm một dòng để xem từng bước), theo bước, theo model, theo key.
- **Nơi lưu:** sổ chi phí nằm ở `data\costs.jsonl`.

### Khác
- Thanh repo trên cùng: xem nhánh hiện tại, đổi nhánh, fetch.
- Nút 🖥 / ☀ / 🌙: giao diện theo hệ thống, sáng hoặc tối.

---

## 4. An toàn

- **AI bị chặn các lệnh git nguy hiểm:** commit, push, reset, checkout, switch, stash, clean, rebase. Chỉ tool mới commit, và chỉ vào nhánh AI.
- **Chỉ push khi bạn bấm:** "Duyệt & push", hoặc tick Push trong hộp commit.
- **AI không thấy mật khẩu DB ghi.** Dữ liệu test chỉ được ghi vào DB đã xác nhận, và phải khớp dự án của task.
- **`appsettings.Local.json` chứa dạng văn bản thường:** session AMIS, API key, mật khẩu tài khoản test và DB. File này đã nằm trong `.gitignore`. **Chỉ dùng trên máy cá nhân, không gửi cho người khác.**
- Không restart tool khi đang có job hoặc chat chạy, vì phiên AI đang chạy sẽ bị dừng.

---

## 5. Gửi tool cho người khác

Khi chép thư mục tool, **bỏ ra**:
- `appsettings.Local.json`: chứa key, session và mật khẩu của bạn.
- `data\`: job, chat, log và chi phí của bạn.
- `bin\`, `obj\`, `sdk-runner\node_modules\`: người nhận tự build và `npm install` lại.

Người nhận:
1. Làm theo mục **1. Cài đặt**.
2. Làm mục **2. Cấu hình lần đầu** với session AMIS và key **của họ**. Nên tạo key riêng cho từng người để dễ theo dõi và thu hồi.
3. Vào tab **Skill**, bấm **Cài mục còn thiếu**.

---

## 6. Lỗi thường gặp

| Lỗi | Cách xử lý |
|---|---|
| `Credit balance is too low` dù key còn tiền | Key chỉ có credit khuyến mãi, loại không áp dụng cho Claude Code. Chuyển sang **API key qua Agent SDK**. Credit khuyến mãi có hạn dùng, xem ở trang Billing |
| Cột AMIS báo lỗi 401 hoặc session | Session AMIS hết hạn. Lấy lại `x-sessionid` (mục 2, bước 1) |
| "… đang có N file sửa chưa commit" | Commit hoặc stash. Nếu là file môi trường thì khai báo ở **File cấu hình local** |
| "Job #X đang giữ repo" | Mở job #X, bấm "Tạo commit & push" hoặc "Commit & trả nhánh" |
| "Chế độ Agent SDK cần Node.js" hoặc thiếu `node_modules` | Cài Node.js, rồi chạy `npm install` trong `sdk-runner` |
| Dừng vì chạm giới hạn chi phí | Tăng giới hạn ở ⚙ Cấu hình › Claude, rồi bấm "Chạy tiếp: review + test" hoặc "Yêu cầu AI sửa…" |
| Review hời hợt, không thấy chạy ship-check | Tab Skill có mục bắt buộc "Chưa cài". Bấm cài |
| Task gắn nhãn ⚠ Khác nhánh | Đổi nhánh repo cho đúng dự án, hoặc ghi nhớ từ khoá nhánh của dự án ngay trong cảnh báo |

---

## 7. Chi tiết kỹ thuật

### Thư mục dữ liệu (`data\`)
| Đường dẫn | Nội dung |
|---|---|
| `jobs\<id>\` | `job.json`, `task.md`, `inputs\`, `analysis.md`, `plan.md`, `review.md`, `test.md`, `pr.md`, `test-artifacts\`, prompt và log từng bước `*.jsonl` |
| `chats\<taskId>\` | Lịch sử chat, tệp đính kèm, `outputs\` |
| `costs.jsonl` | Sổ chi phí: mỗi phiên AI một dòng |

### Bước test
1. Tool lấy các file đã đổi, tìm csproj tương ứng, rồi build bằng `tools\claude-hooks\stop-build.ps1 -WithDependents`. Log ghi ở `build.txt`.
2. Các site IIS (Main, EmpPortal, Hr.Service…) chạy thẳng từ `src\HRM9`, nên sau khi build, web chạy đúng code của nhánh AI. Tool gọi thử các site bị ảnh hưởng.
3. AI viết Playwright `tools\e2e\tests\ai-<taskId>.spec.ts` theo tiêu chí chấp nhận. Tài khoản test được truyền qua biến môi trường, không ghi vào file.
4. AI kiểm dữ liệu bằng `tools\db` (chỉ đọc).
5. Kết quả ghi vào `test.md`: tiêu chí → cách kiểm → kết quả → bằng chứng.
6. Sau khi trả repo về nhánh của bạn, tool build lại để web chạy lại code của bạn.

### API AMIS đang dùng
Gốc `https://amisapp.misa.vn/task/APIS/g1/TaskAPI/api/`, header `x-sessionid`.

| Dữ liệu | API |
|---|---|
| User hiện tại | `GET User/user-info-v2` |
| Việc của tôi theo hạn | `POST Dashboard/getTaskPagingReportMyTask` (`typeTask`: `TotalTaskDue`, `TotalTaskDueSoon`, `TotalTaskExpire`) |
| Chi tiết task | `GET Task/getTaskByIDAsync/{TaskID}` |
| Bình luận | `POST TaskComment/getTaskCommentPaging` |
| Tệp đính kèm | `GET attachment/getAttachmentByTaskID/{TaskID}` |
| Tải tệp | `GET DownloadTMS/50/{AttachmentID}` → token → `GET download/{token}` |

### Cấu hình nâng cao (`appsettings.json` → `Pipeline`)
| Khoá | Ý nghĩa |
|---|---|
| `AllowedTools`, `DisallowedTools` | Công cụ AI được hoặc không được dùng ở bước code (mặc định chặn các lệnh git ghi) |
| `ChatAllowedTools`, `ChatDisallowedTools` | Công cụ của khung chat (chỉ đọc) |
| `StageTimeoutMinutes`, `AnalyzeTimeoutMinutes`, `ChatTimeoutMinutes` | Giới hạn thời gian mỗi bước |
| `MaxFixRounds` | Số vòng sửa theo review |
| `RunTests` | Bật hoặc tắt bước test |
| `BranchPrefix` | Tiền tố tên nhánh AI (`ai/amis-`) |
