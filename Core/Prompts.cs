namespace SuperAutomation.Core;

/// <summary>
/// Prompt cho từng bước. Mỗi bước chạy một phiên Claude Code riêng, thư mục làm việc là hrm-wiki
/// để CLAUDE.md, skill (ship-check, soat-slop…), agent vnr-reviewer và hook của repo tự được nạp.
/// Dòng đánh dấu cuối (STATUS/VERDICT/TEST) được tool đọc để quyết định bước tiếp theo.
/// </summary>
public static class Prompts
{
    /// <summary>Gắn sau mỗi lần bảo AI chạy ship-check: tool đã có bước review độc lập riêng (vnr-reviewer) ngay sau, khỏi chạy reviewer hai lần.</summary>
    public const string NoReviewer = " (bỏ bước 8 \"Review độc lập\" — tool có bước review riêng ngay sau)";

    /// <summary>Danh sách repo AI được sửa: của job (đã chốt lúc tạo nhánh) hoặc theo cấu hình (bước phân tích).</summary>
    private static List<(string Rel, string Path)> Repos(Job j, PipelineOptions o) =>
        new[] { (o.RepoRelativePath, o.RepoPath) }
            .Concat(j.Branch is not null ? j.ExtraRepos.Select(r => (r.Rel, r.Path)) : o.ExtraRepoPaths).ToList();

    private static string RepoNames(Job j, PipelineOptions o) => string.Join(" và ", Repos(j, o).Select(r => r.Path));

    /// <summary>Lệnh xem thay đổi chưa commit của AI so với commit gốc, cho từng repo.</summary>
    private static string DiffCommands(Job j, PipelineOptions o) =>
        string.Join("\n", new[] { $"  git -C \"{o.RepoPath}\" diff {j.BaseSha}" }
            .Concat(j.ExtraRepos.Select(r => $"  git -C \"{r.Path}\" diff {r.BaseSha}")));

    /// <summary>Ràng buộc về file cấu hình local (chỉ khi có khai báo).</summary>
    private static string LocalRule(Job j, PipelineOptions o)
    {
        var lines = Repos(j, o).SelectMany(r => o.LocalFilesOf(r.Rel).Select(f => $"{r.Rel}\\{f.Replace('/', '\\')}")).ToList();
        return lines.Count == 0 ? "" : "\n        - Các file cấu hình môi trường local sau đang được sửa sẵn và KHÔNG thuộc thay đổi của task — không sửa, không đưa vào diff/review: " + string.Join(", ", lines) + ".";
    }

    private static string RepoGuide(Job j, PipelineOptions o)
    {
        var repos = Repos(j, o);
        if (repos.Count == 1) return "";
        return "\n        - Task có thể cần sửa cả backend lẫn frontend: " + string.Join("; ", repos.Select((r, i) => $"{r.Rel}{(i == 0 ? " (repo chính, backend HRM9)" : " (repo phụ)")}")) +
               ". Chỉ sửa repo nào thực sự cần; giữ hợp đồng API giữa hai bên khớp nhau (tên trường, kiểu dữ liệu, URL).";
    }

    private static string AskRule(string dir, bool unattended) => unattended ? $"""
        Khi gặp điểm chưa rõ — CHẠY QUA ĐÊM, không có người trả lời
        - KHÔNG dừng để hỏi. Chọn phương án bạn đề xuất: đúng quy ước repo, an toàn, ít thay đổi nhất, dễ đổi lại sau.
        - Ghi thêm vào {dir}\assumptions.md một mục cho mỗi điểm: đánh số, điểm chưa rõ, các phương án, phương án ĐÃ CHỌN, lý do,
          và nếu người dùng chọn khác thì phải sửa ở đâu. Người dùng sẽ đọc file này sáng hôm sau trước khi duyệt.
        - Chỉ dùng STATUS: NEED_INPUT khi thật sự không thể làm tiếp nếu thiếu thông tin (ví dụ thiếu tài liệu bắt buộc), kèm câu hỏi như bình thường.
        """ : AskRule(dir);

    private static string AskRule(string dir) => $"""
        Khi cần người dùng xác nhận
        - Nếu có điểm trong yêu cầu chưa rõ mà đoán sai sẽ làm sai nghiệp vụ hoặc sai phạm vi (cách hiểu nghiệp vụ, màn/chức năng nào,
          dữ liệu mẫu, chọn giữa các cách làm có hệ quả khác nhau), DỪNG và hỏi — đừng đoán.
        - Ghi câu hỏi vào {dir}\questions.md: đánh số từng câu, mỗi câu nêu ngắn bối cảnh, các phương án bạn thấy và phương án bạn đề xuất,
          để người dùng chỉ cần trả lời "1: A, 2: đồng ý" là đủ.
        - Câu trả lời cuối của bạn khi đó phải là chính danh sách câu hỏi đó (người dùng đọc nó trong khung chat), dòng cuối: STATUS: NEED_INPUT
        - Chỉ hỏi những gì thực sự cần; điểm nhỏ tự quyết theo quy ước của repo và ghi lại giả định trong plan.md.
        - Trước khi hỏi, kiểm analysis.md (Giả định, Cần bổ sung kèm phương án đề xuất), chat.md và related.md (nếu có): điểm đã có câu trả lời
          hoặc đã có phương án đề xuất mà người dùng giao code thì làm theo, KHÔNG hỏi lại. Gom mọi câu hỏi vào MỘT lần dừng.
        """;

    // ---------- Sửa lỗi QC (task "SE xử lý" / "SE xử lý bug") ----------

    private static string QcAnalyze(Job j, string dir) => !j.QcFix ? "" : $"""

        ĐÂY LÀ TASK SỬA LỖI QC — task cha #{j.ParentTaskId} "{j.ParentTaskName}" đã code xong, đã MERGE vào nhánh chính, QC test và trả về.
        - Lỗi KHÔNG nằm ở task này (task con thường trống) mà ở TASK CHA: bình luận mới nhất của QC (test case fail, link, ảnh) và file test case
          Excel đính kèm (bản trích *.md trong inputs\). Có thể đã qua nhiều vòng QC: related.md liệt kê task "SE thực hiện" gốc và các vòng
          "SE xử lý" trước — lỗi vòng này là các test case fail QC báo SAU vòng trước; lỗi đã sửa ở vòng trước thì không liệt kê lại (trừ khi QC báo vẫn lỗi).
        - Không phân tích lại cả tính năng. Mục tiêu: danh sách lỗi QC vòng này, mỗi lỗi một dòng, và hướng sửa.
        Tài liệu phân tích thêm 2 mục (ngoài các mục bên dưới):
        ## Danh sách lỗi QC — đặt ĐẦU tài liệu. Bảng: # | test case (mã/tên) | bước tái hiện | mong đợi | thực tế | phân loại | nguyên nhân dự đoán + file
           Phân loại: BUG (code sai — sẽ sửa) / YÊU CẦU MỚI (ngoài yêu cầu gốc) / MÔI TRƯỜNG-DỮ LIỆU (SP chưa deploy, cấu hình, dữ liệu test) / CHƯA RÕ.
           Tra code gốc đang có trên nhánh hiện tại (git log các file trong related.md) để đoán nguyên nhân, dẫn file thật.
        ## Trả lời QC — bản nháp bình luận gửi QC ở task cha: mỗi lỗi một dòng "TC…: nguyên nhân — sẽ sửa / không phải lỗi vì … (cần BA/QC xác nhận)".
        Tiêu chí chấp nhận = mỗi lỗi BUG: làm đúng bước tái hiện của QC thì ra kết quả mong đợi. "Cần bổ sung" chỉ khi không xác định được lỗi
        (link test case không mở được, thiếu ảnh/bước). REQUIREMENT: CLEAR khi đã xác định được các lỗi BUG cần sửa.

        """;

    private static string QcCode(Job j, string dir) => !j.QcFix ? "" : $"""

        SỬA LỖI QC — không làm lại tính năng
        - Code gốc của task cha đã merge vào nhánh hiện tại. Đọc analysis.md › "Danh sách lỗi QC": CHỈ sửa các lỗi phân loại BUG.
        - Mỗi lỗi: tìm nguyên nhân gốc rồi sửa tối thiểu ở đúng chỗ; không refactor, không viết lại phần đang chạy đúng (QC đã pass các test case khác).
        - Lỗi YÊU CẦU MỚI / MÔI TRƯỜNG-DỮ LIỆU / CHƯA RÕ: không code, ghi lý do.
        - Xong: ghi {dir}\qc-reply.md — bình luận trả lời QC (tiếng Việt, ngắn, không nhắc tên file/code nội bộ): mỗi lỗi một dòng
          "TC… — nguyên nhân: …; đã sửa: …" hoặc "không phải lỗi code vì …, nhờ BA/QC xác nhận". Bước test sẽ cập nhật kết quả kiểm vào file này.

        """;

    private static string QcTest(Job j, string dir) => !j.QcFix ? "" : $"""

            SỬA LỖI QC: kiểm theo ĐÚNG bước tái hiện của QC (analysis.md › Danh sách lỗi QC, file test case trong inputs\) cho từng lỗi BUG —
            đây là tiêu chí quan trọng nhất. Xong cập nhật {dir}\qc-reply.md: thêm kết quả kiểm lại từng lỗi (đạt / chưa đạt + lý do).

            """;

    public static string Analyze(Job j, string dir, PipelineOptions o) => $"""
        Bạn là BA/kỹ sư phân tích yêu cầu cho task AMIS #{j.TaskId}: "{j.TaskName}". Chưa code ở bước này.
        Mục tiêu: đánh giá tài liệu hiện có đã đủ để một lập trình viên code đúng chưa, và nếu chưa thì thiếu gì.

        Nguồn
        - {dir}\task.md: task (và task cha nếu là công việc con — yêu cầu chính thường nằm ở task cha), bình luận, danh sách tệp AMIS.
        - {dir}\inputs\: tệp người dùng upload, ảnh trong mô tả; tệp Office đã được trích thành *.md cạnh tệp gốc. Đọc hết, kể cả ảnh.
        - {dir}\chat.md: trao đổi với người dùng — câu trả lời và quyết định ở đây được ưu tiên hơn mô tả gốc.
        - Nghiệp vụ: CLAUDE.md và wiki trong {o.WorkspaceRoot}. Source: {RepoNames(j, o)} — tra để biết màn/chức năng/SP nào bị ảnh hưởng (backend lẫn frontend).
        - {dir}\related.md (nếu có): các task con khác của cùng task cha mà tool đã làm — câu trả lời, quyết định của người dùng/BA ở đó thường áp dụng luôn cho task này.
        {(j.AnalyzeRounds > 1 ? $"- Đây là lần phân tích thứ {j.AnalyzeRounds}. Bản trước: {Path.Combine(dir, "analysis.prev.md")} — đối chiếu xem người dùng đã trả lời/bổ sung đủ các điểm thiếu chưa. KHÔNG hỏi lại điểm đã được trả lời; chỉ hỏi điểm mới thật sự phát sinh từ câu trả lời." : "")}

        Tự tìm câu trả lời trước khi hỏi — mỗi lần hỏi lại làm task chậm hàng giờ
        Trước khi đưa một điểm vào "Cần bổ sung", tra lần lượt: chat.md → task, task cha, bình luận, tệp đính kèm → related.md (task anh em)
        → source hiện tại (màn/chức năng tương tự đang xử lý trường hợp này thế nào) → wiki.
        Tìm được thì ghi vào "Giả định" kèm nguồn (ví dụ "theo bình luận của BA ngày …", "theo task #…", "giống màn X: file Y"), KHÔNG hỏi.
        Chỉ hỏi điểm không tra được ở đâu VÀ đoán sai thì sai nghiệp vụ/phạm vi. Gom mọi câu hỏi trong một lần — không để dành cho vòng sau.

        Chỉ đọc: không sửa file, không chạy lệnh làm thay đổi repo.
        {DbReadRule}
        {QcAnalyze(j, dir)}

        Câu trả lời cuối của bạn chính là tài liệu phân tích (tool lưu thành analysis.md), viết tiếng Việt, gồm các mục:
        ## Hiểu yêu cầu — tóm tắt bằng lời của bạn: ai dùng, làm gì, kết quả mong đợi
        ## Phạm vi và nơi cần sửa — màn/chức năng, file/SP/bảng dự kiến (dẫn đường dẫn thật trong source).
           Cuối mục có dòng "File dự kiến:" rồi liệt kê mỗi file một dòng, đường dẫn tương đối repo (ví dụ src/HRM9/.../X.cs), kể cả file .sql của store.
        ## Tiêu chí chấp nhận — checklist kiểm được
        ## Giả định — các điểm nhỏ bạn tự quyết theo quy ước repo
        ## Cần bổ sung — đánh số; mỗi mục nêu bối cảnh, các phương án và phương án đề xuất để người dùng chỉ cần trả lời "1: A".
           Nếu task.md liệt kê tệp đính kèm AMIS mà chưa có bản trong inputs\ và mô tả không đủ thay thế, ghi rõ tên tệp cần upload.
           Ghi "(không có)" nếu đủ.
        ## Tin nhắn gửi BA — chỉ khi còn mục "Cần bổ sung": một tin nhắn ngắn, lịch sự, người dùng copy gửi thẳng BA/khách hàng.
           Mở đầu 1 câu nêu task; liệt kê từng câu hỏi đánh số bằng ngôn ngữ nghiệp vụ (không nhắc tên file/code), mỗi câu kèm phương án đề xuất
           để BA chỉ cần trả lời "đồng ý" hoặc chọn; xin gửi kèm tệp còn thiếu nếu có. Ghi "(không có)" nếu yêu cầu đã rõ.

        Kết luận UNCLEAR chỉ khi có điểm mà đoán sai sẽ làm sai nghiệp vụ hoặc sai phạm vi; điểm nhỏ đưa vào Giả định.
        Dòng cuối cùng phải đúng một trong:
        REQUIREMENT: CLEAR
        REQUIREMENT: UNCLEAR
        """;

    public static string Code(Job j, string dir, PipelineOptions o) => $"""
        Bạn là lập trình viên tự động cho task AMIS #{j.TaskId}: "{j.TaskName}".

        Bối cảnh
        - Thư mục làm việc: {o.WorkspaceRoot} — làm theo CLAUDE.md ở đây.
        - Repo cần sửa: {RepoNames(j, o)}. Nhánh {j.Branch} đã được tạo sẵn ở mọi repo (repo chính từ {j.BaseBranch}).{RepoGuide(j, o)}
        - Tài liệu yêu cầu: {dir}\task.md. Tệp kèm theo (nếu có): {dir}\inputs\. Dữ liệu gốc từ AMIS: {dir}\task.raw.json.
        - Trao đổi trước đó giữa người dùng và AI về task (nếu có): {dir}\chat.md — các quyết định đã chốt ở đây được ưu tiên hơn mô tả gốc.
        - Phân tích yêu cầu đã được người dùng duyệt (nếu có): {dir}\analysis.md — phạm vi, tiêu chí chấp nhận, giả định.
        - Task anh em cùng task cha (nếu có): {dir}\related.md — câu trả lời, quyết định đã chốt ở đó dùng lại được, không hỏi lại.
          {(j.Requirement == "UNCLEAR" ? "Lưu ý: người dùng giao code dù phân tích còn mục \"Cần bổ sung\"; với các mục đó, chọn phương án đề xuất trong analysis.md và ghi vào plan.md." : "")}

        Việc cần làm
        1. Đọc kỹ analysis.md, task.md, chat.md và các tệp trong inputs\ (kể cả ảnh, bản trích *.md), tra wiki theo CLAUDE.md để hiểu nghiệp vụ.
        2. Ghi kế hoạch ngắn vào {dir}\plan.md: hiểu yêu cầu thế nào, giả định, file sẽ sửa, cách làm, rủi ro.
        3. Implement trong {RepoNames(j, o)} theo quy ước của từng repo.
        4. Chạy skill ship-check{NoReviewer} và sửa cho tới khi đạt.
        {QcCode(j, dir)}

        {AskRule(dir, j.Unattended)}

        Ràng buộc
        - Không commit, không push, không đổi nhánh, không stash, không reset. Tool sẽ tự commit vào nhánh {j.Branch}.
        - Không sửa file ngoài {RepoNames(j, o)} và {dir}.{LocalRule(j, o)}

        Khi xong: tóm tắt thay đổi (file nào, vì sao). Dòng cuối cùng phải đúng một trong:
        STATUS: DONE
        STATUS: NEED_INPUT
        STATUS: BLOCKED   (không thể làm vì lý do khác câu hỏi nghiệp vụ, ví dụ thiếu quyền/môi trường — nêu rõ lý do)
        """;

    public static string Resume(Job j, string dir, PipelineOptions o, string answer) => $"""
        Người dùng đã trả lời các câu hỏi của bạn cho task AMIS #{j.TaskId}:

        {answer}

        Repo {RepoNames(j, o)} đang ở lại nhánh {j.Branch}, gồm phần bạn đã làm trước đó (đã được commit WIP).
        Toàn bộ trao đổi đã được cập nhật ở {dir}\chat.md.

        Tiếp tục công việc: cập nhật {dir}\plan.md theo câu trả lời, implement, chạy ship-check{NoReviewer}.
        {(j.Unattended ? $"Chạy qua đêm: không dừng hỏi nữa — điểm còn chưa rõ thì chọn phương án đề xuất và ghi vào {dir}\\assumptions.md (đã chọn gì, vì sao)." : $"Nếu vẫn còn điểm cần xác nhận, làm như trước: ghi {dir}\\questions.md và dừng.")}
        Ràng buộc như trước: không commit, không push, không đổi nhánh, không stash, không reset.{LocalRule(j, o)}

        Dòng cuối cùng: STATUS: DONE, STATUS: NEED_INPUT hoặc STATUS: BLOCKED
        """;

    /// <summary>Người dùng xem kết quả xong và muốn sửa/thêm logic trên code AI đang ở nhánh AI.</summary>
    public static string Revise(Job j, string dir, PipelineOptions o, string request) => $"""
        Sửa thêm code cho task AMIS #{j.TaskId}: "{j.TaskName}" theo yêu cầu MỚI của người dùng.

        Yêu cầu của người dùng (ưu tiên cao nhất):
        {request}

        Bối cảnh
        - Thư mục làm việc: {o.WorkspaceRoot} — làm theo CLAUDE.md ở đây.
        - Repo: {RepoNames(j, o)}, đang ở nhánh {j.Branch}, chứa phần code AI đã làm trước đó (xem bằng):
        {DiffCommands(j, o)}
        - Yêu cầu gốc: {dir}\task.md, phân tích {dir}\analysis.md, kế hoạch {dir}\plan.md.
        - Toàn bộ trao đổi với người dùng: {dir}\chat.md — đọc kỹ phần CUỐI, nơi người dùng và AI chat vừa chốt chi tiết
          (vị trí đặt code, ngưỡng, định dạng log…). Yêu cầu trên có thể chỉ ngắn gọn "làm theo chat", khi đó lấy chi tiết từ chat.md.
        - Kết quả review/test lần trước: {dir}\review.md, {dir}\test.md (nếu có).

        Việc cần làm
        1. Đọc diff hiện tại và chat.md để hiểu code đang có và điều người dùng muốn đổi.
        2. Sửa/thêm code đúng yêu cầu, giữ nguyên phần đã làm đúng. Không làm thừa ngoài yêu cầu.
        3. Ghi thêm vào cuối {dir}\plan.md mục "Sửa thêm (lần {j.Revisions})": yêu cầu, file đã sửa, cách làm.
        4. Chạy skill ship-check{NoReviewer} và sửa cho tới khi đạt.

        {AskRule(dir)}

        Ràng buộc
        - Không commit, không push, không đổi nhánh, không stash, không reset. Tool sẽ commit khi người dùng xác nhận.
        - Không sửa file ngoài {RepoNames(j, o)} và {dir}.{LocalRule(j, o)}

        Khi xong: tóm tắt những gì đã sửa thêm (file nào, vì sao). Dòng cuối cùng phải đúng một trong:
        STATUS: DONE
        STATUS: NEED_INPUT
        STATUS: BLOCKED
        """;

    /// <summary>
    /// Khung mô tả PR (pr.md, markdown — TFS hiển thị được) theo mẫu PR của team: 3 mục có emoji.
    /// Thân commit dùng bản văn bản thuần chuyển từ file này (JobRunner.PrPlain).
    /// </summary>
    private static string PrFormat(Job j, string dir) => $"""
        Viết thêm {dir}\pr.md: MÔ TẢ PULL REQUEST (markdown — TFS hiển thị được), cũng dùng làm thân commit. Đúng khung 3 mục sau,
        mỗi mục là các gạch đầu dòng "- " ngắn gọn; tên file/bảng/cột/hàm/mã đặt trong `...`; nhãn nhóm thay đổi in đậm **...**.
        TỔNG ĐỘ DÀI tối đa khoảng 1500 ký tự (TFS giới hạn mô tả PR): mỗi mục 2–5 gạch, mỗi gạch một câu, không liệt kê từng dòng code —
        chi tiết đã có trong diff và tài liệu task:

        ## 🐛 Nguyên nhân gốc & Hiện trạng
        - Đường dẫn màn hình (Menu > … > **Tên màn**): hiện trạng đang thế nào, vì sao cần sửa{(j.QcFix ? " — với task sửa lỗi QC: từng lỗi QC báo (mã test case) và NGUYÊN NHÂN GỐC trong code" : "")}.
        - Thông tin nền nếu cần (nguồn wiki/tài liệu, ví dụ "wiki du-an/tra/xyz293 mục 4").

        ## 🎯 Mục đích & Thay đổi
        - Mỗi dòng một nhóm thay đổi, theo thứ tự: **Script DB** (`đường dẫn script`): …; **Store** `tên.sql`: …; Entity/model/edmx; view `.cshtml`;
          controller/service `Lớp.Hàm`: sửa gì, điều kiện ra sao.
        - Dòng cuối (nếu có): lý do của quyết định thiết kế không hiển nhiên.

        ## 🔍 Ảnh hưởng & Kiểm tra
        - Ảnh hưởng: màn/chức năng/hàm dùng chung bị đụng tới, những gì giữ nguyên. Rủi ro / việc cần làm khi deploy (chạy script DB trước, bật cột…).
        - Test local: (để trống — bước test sẽ điền kết quả thật)

        Mẫu (rút gọn):
        ## 🐛 Nguyên nhân gốc & Hiện trạng
        - Màn Đào tạo > Danh mục > **DS chứng nhận/chứng chỉ**: popup Tạo mới/Chỉnh sửa chỉ có ô "Số ngày làm việc" (`So_ngay_lam_viec`), chưa có Đơn vị.
        ## 🎯 Mục đích & Thay đổi
        - **Script DB** (`Updates/Scripts/SQL/20261118_01.sql`): thêm cột `Tra_Certificate.OJTDayUnit nvarchar(50) NULL` + 2 dòng enum.
        - **Store** `01580_hrm_tra_sp_get_Certificate.sql`: thêm `OJTDayView` (số + " " + đơn vị).
        - `TraCertificateInfo.cshtml`: dropdown Đơn vị đặt cạnh ô Số ngày làm việc.
        ## 🔍 Ảnh hưởng & Kiểm tra
        - Ảnh hưởng: chỉ màn DS chứng chỉ (popup, lưới, xuất, nhập). Rủi ro: cần chạy script DB trước khi cập nhật code.
        - Test local: (bước test điền)

        Viết theo code THỰC TẾ trong diff (kể cả phần sửa sau review); không nhắc AI/tool/pipeline; không ghi tiêu đề PR (tool tự đặt).
        """;

    /// <summary>Tham số model khi gọi Agent vnr-reviewer (tham số model của Agent đè model trong file agent).</summary>
    private static string ReviewerModelArg(PipelineOptions o) =>
        o.ReviewerModel is "sonnet" or "opus" ? $" (gọi Agent với tham số model: \"{o.ReviewerModel}\")" : "";

    public static string Review(Job j, string dir, PipelineOptions o) => $"""
        Review độc lập thay đổi cho task AMIS #{j.TaskId}: "{j.TaskName}". Bạn không phải người viết code này.

        - Yêu cầu: {dir}\task.md (và {dir}\inputs\), phân tích đã duyệt {dir}\analysis.md (tiêu chí chấp nhận),
          các quyết định đã chốt với người dùng: {dir}\chat.md (nếu có). Kế hoạch của người code: {dir}\plan.md.
        - Thay đổi: mọi thay đổi chưa commit so với commit gốc, cộng các file mới chưa track:
        {DiffCommands(j, o)}

        Cách làm
        1. Dùng agent vnr-reviewer{ReviewerModelArg(o)} để review nghiệp vụ, và skill soat-slop cho diff.
           Gọi vnr-reviewer ở chế độ CHỜ KẾT QUẢ (không chạy nền): phiên này kết thúc là tool đọc kết luận ngay,
           nên không được dừng ở câu "đang chờ reviewer" — phải có kết quả, ghi review.md và dòng VERDICT rồi mới kết thúc.
        2. Đối chiếu từng yêu cầu trong task.md và chat.md: đã làm đủ chưa, có làm thừa hay sai phạm vi không.
        3. Đối chiếu CHUẨN CÔNG TY mà tool review PR (PR Off-Duty) dùng, chỉ xét đoạn code thay đổi:
           - {Standards.PrGuide} (mục 3 "Bốn nhóm lỗi luôn chặn merge", mục 4 bảo mật/hiệu năng),
           - {Standards.SecurityPerformance} (SP-01→29), {Standards.Naming} (N8: log debug, TODO:, FIXME, file rác).
           Vi phạm chặn merge (N8, secret/connection string, nối chuỗi SQL từ input, endpoint mới thiếu [Authorize]/kiểm quyền,
           breaking change chữ ký public/schema, truy vấn DB/Redis trong vòng lặp kể cả gọi gián tiếp) → đưa vào "Lỗi phải sửa", ghi mã điều (N8, SP-10…).
           Tên commit do tool tự đặt theo chuẩn, không cần xét.
        {(j.QcFix ? "4. Task SỬA LỖI QC: đối chiếu analysis.md › \"Danh sách lỗi QC\" — mỗi lỗi BUG đã được sửa đúng nguyên nhân chưa; sửa có làm thừa, đụng phần QC đã pass (có thể làm hỏng test case khác) không. Lỗi chưa sửa hoặc sửa lan rộng → \"Lỗi phải sửa\"." : "")}

        Chỉ đọc: không sửa file nào trong repo.{LocalRule(j, o)}

        {PrFormat(j, dir)}

        Ghi kết quả vào {dir}\review.md gồm ba mục: "Thiếu so với yêu cầu", "Lỗi phải sửa" (file:dòng, lý do, cách sửa),
        "Góp ý không bắt buộc". Dòng cuối cùng của câu trả lời phải là
        VERDICT: PASS  (không có lỗi phải sửa và không thiếu yêu cầu)
        hoặc
        VERDICT: FAIL
        """;

    /// <summary>Review nhẹ cho diff ít rủi ro (không SQL/quyền/luồng duyệt, ít dòng): một phiên, không gọi vnr-reviewer; nghi ngờ thì ESCALATE để tool chạy review đầy đủ.</summary>
    public static string ReviewLight(Job j, string dir, PipelineOptions o) => $"""
        Review độc lập (mức NHẸ) thay đổi cho task AMIS #{j.TaskId}: "{j.TaskName}". Bạn không phải người viết code này.
        Tool đã xếp thay đổi này vào loại ít rủi ro (không đổi SQL, không đụng quyền/menu/luồng duyệt, ít dòng) nên chỉ review một phiên, tự làm, KHÔNG gọi agent vnr-reviewer.

        - Yêu cầu: {dir}\task.md (và {dir}\inputs\), phân tích đã duyệt {dir}\analysis.md (tiêu chí chấp nhận), quyết định đã chốt: {dir}\chat.md (nếu có). Kế hoạch: {dir}\plan.md.
        - Thay đổi:
        {DiffCommands(j, o)}

        Cách làm
        1. Đọc hết diff. Đối chiếu từng yêu cầu trong task.md và chat.md: đã làm đủ chưa, có làm thừa hay sai phạm vi không.
        2. Mỗi hàm/action/property bị sửa: Grep nơi gọi — chỗ gọi khác có bị ảnh hưởng không; cùng dữ liệu có đường song song (xuất Excel, chi tiết, API App/Portal) cần sửa theo mà chưa sửa không.
        3. Skill soat-slop cho diff.
        4. Chuẩn công ty, chỉ xét đoạn thay đổi: {Standards.SecurityPerformance} (SP-01→29), {Standards.Naming} (N8: log debug, TODO:, FIXME, file rác).
           Vi phạm chặn merge (N8, secret, nối chuỗi SQL từ input, endpoint thiếu [Authorize]/kiểm quyền, breaking change chữ ký public, truy vấn DB trong vòng lặp) → "Lỗi phải sửa", ghi mã điều.
        {(j.QcFix ? "5. Task SỬA LỖI QC: đối chiếu analysis.md › \"Danh sách lỗi QC\" — mỗi lỗi BUG đã được sửa đúng nguyên nhân chưa, có đụng phần QC đã pass không." : "")}

        NÂNG MỨC: nếu thấy thay đổi rủi ro hơn tool đánh giá — đổi chữ ký hàm public / hàm dùng chung nhiều nơi gọi, đụng dữ liệu nhiều công ty / quyền dữ liệu,
        logic nghiệp vụ khó mà bạn không chắc đúng sau khi đọc — thì KHÔNG đoán: ghi lý do vào {dir}\review.md rồi kết thúc bằng dòng VERDICT: ESCALATE
        (tool sẽ chạy review đầy đủ có vnr-reviewer; khi đó khỏi viết pr.md).

        Chỉ đọc: không sửa file nào trong repo.{LocalRule(j, o)}

        {PrFormat(j, dir)}

        Ghi kết quả vào {dir}\review.md gồm ba mục: "Thiếu so với yêu cầu", "Lỗi phải sửa" (file:dòng, lý do, cách sửa), "Góp ý không bắt buộc"; ghi rõ ở đầu là "Review nhẹ".
        Dòng cuối cùng của câu trả lời phải là một trong:
        VERDICT: PASS      (không có lỗi phải sửa và không thiếu yêu cầu)
        VERDICT: FAIL
        VERDICT: ESCALATE  (cần review đầy đủ — xem mục NÂNG MỨC)
        """;

    public static string Fix(Job j, string dir, PipelineOptions o) => $"""
        Sửa theo kết quả review cho task AMIS #{j.TaskId}: "{j.TaskName}".

        - Yêu cầu: {dir}\task.md, quyết định đã chốt: {dir}\chat.md (nếu có). Kết quả review: {dir}\review.md.
        - Repo: {RepoNames(j, o)}, nhánh {j.Branch}.

        Sửa mọi mục trong "Lỗi phải sửa" và "Thiếu so với yêu cầu". Chỉ làm mục "Góp ý không bắt buộc" khi chắc chắn đúng.
        Chạy lại skill ship-check{NoReviewer}. Ràng buộc như trước: không commit, không push, không đổi nhánh, không stash, không reset.
        Ghi những gì đã sửa vào cuối {dir}\review.md dưới tiêu đề "Đã sửa (vòng {j.FixRounds})".

        Dòng cuối cùng: STATUS: DONE hoặc STATUS: BLOCKED
        """;

    /// <summary>Giải conflict sau khi tool gộp nhánh gốc mới nhất vào nhánh AI (merge đang dở, chưa commit).</summary>
    public static string Merge(Job j, string dir, PipelineOptions o, string conflicts) => $"""
        Giải CONFLICT cho nhánh {j.Branch} (task AMIS #{j.TaskId}: "{j.TaskName}").
        Tool vừa chạy `git merge --no-commit` nhánh gốc mới nhất vào nhánh AI; merge đang dở với các file conflict:
        {conflicts}

        - Yêu cầu của task: {dir}\task.md, {dir}\analysis.md. Phần AI đã làm: {dir}\code.result.md, {dir}\pr.md.
        - Phía nhánh gốc là code người khác đã merge (task khác) — PHẢI giữ nguyên ý của họ; phía nhánh AI là thay đổi của task này.

        Cách làm cho từng file:
        1. Đọc cả hai phía (git diff, git log -p nhánh gốc cho file đó) để hiểu mỗi bên đổi gì và vì sao.
        2. Gộp sao cho GIỮ CẢ HAI thay đổi (thường: lấy bản nhánh gốc rồi áp lại phần của task này). Xoá hết dấu <<<<<<< ======= >>>>>>>.
        3. Store SQL: giữ logic mới của nhánh gốc + phần sửa của task; file .sql giữ đúng encoding gốc của file.
        4. Build lại project bị ảnh hưởng (skill ship-check, bước build) cho tới khi đạt.
        Không commit, không merge/rebase/reset/stash/đổi nhánh, không `git merge --abort` — tool tự commit khi bạn xong.
        Ghi {dir}\merge.md: từng file conflict → hai phía đổi gì → đã gộp thế nào.

        Dòng cuối cùng: MERGE: DONE (đã giải hết, build đạt) hoặc MERGE: BLOCKED (kèm lý do — vd hai phía mâu thuẫn nghiệp vụ, cần người quyết)
        """;

    /// <summary>Review gọn sau vòng tự sửa theo test: chỉ phần vừa sửa (testfix.diff) và vùng bị ảnh hưởng.</summary>
    public static string ReviewDelta(Job j, string dir, PipelineOptions o) => $"""
        Review PHẦN VỪA SỬA theo kết quả test cho task AMIS #{j.TaskId}: "{j.TaskName}". Bạn không phải người viết code này.
        Toàn bộ task đã được review trước đó (kết quả: {dir}\review.md) — lần này CHỈ xét phần sửa mới.

        - Phần vừa sửa: {dir}\testfix.diff (diff so với ngay trước vòng sửa, kèm danh sách file mới nếu có).
        - Lý do sửa, cách tái hiện lỗi, nguyên nhân gốc: {dir}\testfix.md. Test case liên quan: {dir}\testcases.json.
        - Yêu cầu gốc nếu cần đối chiếu: {dir}\task.md, {dir}\analysis.md, {dir}\chat.md.

        Cách làm
        1. Sửa có đúng nguyên nhân gốc ghi trong testfix.md không, hay chỉ che triệu chứng (dùng skill soat-slop cho testfix.diff).
        2. Ảnh hưởng: hàm/store vừa sửa còn được gọi ở đâu (Grep / gitnexus impact) — sửa có làm sai chỗ gọi khác hoặc phần đã PASS không.
        3. Chuẩn công ty cho đoạn vừa sửa: {Standards.SecurityPerformance} (SP-01→29), {Standards.Naming} (N8).
        Chỉ đọc: không sửa file nào trong repo.{LocalRule(j, o)}

        Nối thêm vào CUỐI {dir}\review.md mục "## Review vòng sửa theo test" gồm: "Lỗi phải sửa" (file:dòng, lý do, cách sửa) và "Ghi chú".
        Không xoá các mục cũ của review.md.
        Dòng cuối cùng: VERDICT: PASS hoặc VERDICT: FAIL
        """;

    /// <summary>Review lại sau vòng sửa theo review: chỉ xét các lỗi đã nêu đã được sửa đúng chưa và phần vừa sửa (reviewfix.diff), không review lại cả task.</summary>
    public static string ReviewFixDelta(Job j, string dir, PipelineOptions o) => $"""
        Review lại PHẦN VỪA SỬA theo kết quả review cho task AMIS #{j.TaskId}: "{j.TaskName}". Bạn không phải người viết code này.
        Toàn bộ task đã được review ở lượt trước — kết quả và các lỗi đã nêu ở {dir}\review.md (mục "Đã sửa (vòng {j.FixRounds})" ở cuối là phần người code ghi lại).
        Lần này CHỈ xét: các mục "Lỗi phải sửa" / "Thiếu so với yêu cầu" của lượt trước, và phần code vừa sửa.

        - Phần vừa sửa: {dir}\reviewfix.diff (diff so với ngay trước vòng sửa, kèm danh sách file mới nếu có).
        - Yêu cầu gốc nếu cần đối chiếu: {dir}\task.md, {dir}\analysis.md, {dir}\chat.md.

        Cách làm (tự làm trong phiên này, không gọi agent vnr-reviewer)
        1. Từng mục "Lỗi phải sửa" / "Thiếu so với yêu cầu" lượt trước: đã sửa đúng nguyên nhân chưa (mở code thật ở file:dòng), hay chỉ che triệu chứng.
        2. Phần vừa sửa: skill soat-slop cho reviewfix.diff; hàm/store vừa sửa còn được gọi ở đâu (Grep / gitnexus impact) — có làm sai chỗ gọi khác không.
        3. Chuẩn công ty cho đoạn vừa sửa: {Standards.SecurityPerformance} (SP-01→29), {Standards.Naming} (N8).
        4. Phần sửa có đổi điều mà {dir}\pr.md đang mô tả thì cập nhật pr.md cho khớp (giữ khung 3 mục); không thì để nguyên.
        Chỉ đọc: không sửa file nào trong repo.{LocalRule(j, o)}

        Nối thêm vào CUỐI {dir}\review.md mục "## Review lại sau vòng sửa {j.FixRounds}" gồm: "Lỗi phải sửa" (file:dòng, lý do, cách sửa) và "Ghi chú".
        Không xoá các mục cũ của review.md.
        Dòng cuối cùng: VERDICT: PASS (mọi lỗi lượt trước đã sửa đúng, phần sửa không sinh lỗi mới) hoặc VERDICT: FAIL
        """;

    /// <summary>Sửa theo kết quả test FAIL: bắt buộc tái hiện lỗi trước khi sửa; không tái hiện được thì không sửa.</summary>
    public static string TestFix(Job j, string dir, PipelineOptions o, string failed) => $"""
        Sửa lỗi mà bước TEST phát hiện cho task AMIS #{j.TaskId}: "{j.TaskName}".

        - Kết quả test: {dir}\test.md, test case: {dir}\testcases.json. Yêu cầu: {dir}\task.md, tiêu chí: {dir}\analysis.md, quyết định đã chốt: {dir}\chat.md (nếu có).
        - Repo: {RepoNames(j, o)}, nhánh {j.Branch}. Thay đổi hiện có:
        {DiffCommands(j, o)}
        - Test case FAIL cần xử lý:
        {failed}
        {(j.TestFixRounds > 1 ? $"- Đây là VÒNG TỰ SỬA {j.TestFixRounds}: vòng trước đã sửa và giảm được số test case FAIL nhưng chưa hết. Đọc {dir}\\testfix.md (vòng trước) trước — không lặp lại cách sửa đã không hiệu quả; test case vừa mới FAIL (trước đó PASS) có thể do chính lần sửa trước gây ra. GHI NỐI vào cuối testfix.md mục \"## Vòng {j.TestFixRounds}\", không xoá nội dung cũ." : "")}

        Cách làm, theo thứ tự, cho từng test case FAIL:
        1. TÁI HIỆN: chứng minh lỗi bằng truy vấn DB / EXEC store / gọi API qua {o.WorkspaceRoot}\tools\db (q.ps1, sp.ps1) — ra đúng con số/dòng sai như test ghi.
           Ghi lệnh + kết quả vào {dir}\testfix.md.
        2. NGUYÊN NHÂN GỐC: chỉ ra file:dòng gây lỗi và vì sao (ghi vào testfix.md).
        3. SỬA tối thiểu đúng chỗ đó; chạy lại truy vấn/API ở bước 1 để thấy kết quả đã đúng. Store đã sửa: ghi rõ tên store (tool sẽ deploy lại khi test).
        Test case không tái hiện được, hoặc lỗi nằm ở môi trường/dữ liệu test chứ không ở code: KHÔNG sửa code cho nó, ghi lý do vào testfix.md.
        Chỉ đụng phần liên quan tới các test case trên; giữ nguyên phần đã PASS. Chạy lại skill ship-check cho phần vừa sửa{NoReviewer}.
        Ràng buộc như trước: không commit, không push, không đổi nhánh, không stash, không reset.

        Dòng cuối cùng (một trong):
        FIX: DONE              (đã sửa ít nhất một lỗi đã tái hiện được)
        FIX: NOT_REPRODUCED    (không tái hiện được lỗi nào — không sửa gì)
        FIX: NOT_CODE          (lỗi do môi trường/dữ liệu test — không sửa gì)
        """;

    /// <summary>Soạn dữ liệu test: AI tìm dữ liệu sẵn có (chỉ đọc), thiếu thì viết script INSERT + script dọn; tool kiểm tra và tự chạy.</summary>
    /// <summary>Vai trò trong kịch bản test: admin đóng vai được thì dùng admin; cần user thiếu quyền mà chưa có tài khoản → tạo tài khoản tạm (nếu cho phép).</summary>
    public static string RolesRule(string defaultUser, IEnumerable<string> accounts, bool tempAccounts, long taskId, string marker)
    {
        var list = string.Join(", ", new[] { defaultUser + " (mặc định)" }.Concat(accounts));
        var sb = new System.Text.StringBuilder();
        sb.Append($"   VAI TRÒ / TÀI KHOẢN — tài khoản đã khai báo: {list}.\n");
        sb.Append($"   a) Vai trò tài khoản mặc định {defaultUser} đóng được thì cho nó đóng: người đăng ký/nhận thông báo = HỒ SƠ NHÂN VIÊN gắn với {defaultUser}\n");
        sb.Append("      (tra ProfileID của user đó), người duyệt = user đó trong cấu hình duyệt của dữ liệu tạo ra. Không cần tài khoản riêng cho các vai này.\n");
        if (tempAccounts)
        {
            sb.Append($"   b) Vai trò cần quyền KHÁC {defaultUser} (chỉ có một quyền, không có quyền, người khác phòng ban…) mà chưa có tài khoản khai báo:\n");
            sb.Append($"      TẠO TÀI KHOẢN TẠM bằng INSERT trong test-data.sql — UserLogin \"ai.test.{taskId}.<vai>\" (vd ai.test.{taskId}.duyet, ai.test.{taskId}.noperm),\n");
            sb.Append($"      tên hiển thị chứa {marker}. Chép từ dòng user của {defaultUser}: MỌI cột mật khẩu/hash/salt/trạng thái kích hoạt/ngôn ngữ (đăng nhập bằng\n");
            sb.Append($"      chính mật khẩu của {defaultUser}); ID mới NEWID(). KHÔNG chép nhóm quyền của {defaultUser}: chỉ gán đúng quyền kịch bản cần (thêm vào nhóm có sẵn\n");
            sb.Append($"      đúng quyền đó, hoặc tạo nhóm quyền riêng tên chứa {marker}); gắn hồ sơ nhân viên/phân quyền dữ liệu nếu kịch bản cần. Tra cấu trúc bảng user,\n");
            sb.Append("      nhóm, quyền (và bảng tài khoản của Identity Server nếu đăng nhập SSO) trước khi viết. Không sửa user/nhóm có sẵn. Cleanup xoá theo ID đã tạo.\n");
            sb.Append("      Ghi vào test-data.md mục \"## Tài khoản tạm\": mỗi dòng `ai.test.…` — vai trò — quyền đã gán. Đã có từ lần trước thì dùng lại, không tạo lại.\n");
        }
        else
            sb.Append("   b) Vai trò cần quyền khác mà chưa có tài khoản: ghi trong test-data.md \"cần tài khoản <vai trò/quyền> — thêm ở Cấu hình › Test\" (không tạo user).\n");
        return sb.ToString();
    }

    public static string TestData(Job j, string dir, PipelineOptions o, string database, string marker, bool allowExec, string rolesRule = "") => $"""
        Chuẩn bị DỮ LIỆU TEST cho task AMIS #{j.TaskId}: "{j.TaskName}". Chưa test ở bước này.

        - Tiêu chí chấp nhận: {dir}\analysis.md. Yêu cầu: {dir}\task.md, quyết định đã chốt: {dir}\chat.md (nếu có).
        - Thay đổi của AI:
        {DiffCommands(j, o)}
        - DB test: {database}. Bạn chỉ được ĐỌC qua {o.WorkspaceRoot}\tools\db (q.ps1, sp.ps1). Không tự chạy lệnh ghi bằng bất kỳ cách nào
          (sqlcmd, script khác…) — tool sẽ kiểm tra và chạy script bạn viết, trong một transaction.

        Cách làm
        1. Với từng tiêu chí chấp nhận, tìm dữ liệu CÓ SẴN dùng được (truy vấn chỉ đọc). Có thì dùng, ghi rõ mã/ID cụ thể.
        2. Chỉ với tiêu chí thiếu dữ liệu, viết:
           - {dir}\test-data.sql: chỉ INSERT (được DECLARE/SET/SELECT/IF, bảng tạm #…{(allowExec ? ", EXEC stored procedure nghiệp vụ nếu thật cần" : "; KHÔNG EXEC")}).
             Không UPDATE/DELETE dữ liệu có sẵn — cần trạng thái khác thì tạo bản ghi mới.
             Mọi bản ghi tạo ra phải chứa chuỗi {marker} ở một cột chữ (mã, tên, ghi chú) để dọn được; khoá GUID dùng NEWID();
             tra cấu trúc bảng (INFORMATION_SCHEMA, ràng buộc khoá ngoại, cột NOT NULL, IsDelete) trước khi viết.
           - {dir}\test-cleanup.sql: DELETE (hoặc UPDATE) có WHERE nhắm đúng các bản ghi trên (theo {marker} hoặc ID đã tạo), xoá bảng con trước.
             DB dùng chung, đang có web chạy: mỗi lệnh xoá phải dùng được index — lọc theo khoá chính/khoá ngoại (ID, RecordID, ProfileID… = giá trị cụ thể
             hoặc IN (SELECT ID FROM bảng cha WHERE …)). Bảng lớn (Sys_Notification, Sys_ProcessSendMail, log/lịch sử): KHÔNG lọc bằng LIKE, hàm bọc cột
             (LOWER/CONVERT/CAST trên cột) hay OR nhiều nhánh — các kiểu này quét cả bảng, khoá bảng hàng phút và làm web treo.
             Cách chắc nhất: trong test-data.sql ghi ID vừa tạo vào bảng chỉ định bằng marker có index sẵn (vd Comment của phiếu), rồi cleanup xoá theo ID đó.
           Phân tách batch bằng dòng GO nếu cần. Tool từ chối script có DROP/ALTER/TRUNCATE/SQL động hoặc ghi quá giới hạn số dòng.
        {rolesRule}
        3. Ghi {dir}\test-data.md: bảng tiêu chí → dữ liệu dùng để test (có sẵn hay tạo mới, mã nhân viên/kỳ/ID cụ thể) → kết quả mong đợi.

        Dòng cuối cùng của câu trả lời phải là một trong:
        DATA: READY      (đã viết test-data.sql + test-cleanup.sql)
        DATA: EXISTING   (dữ liệu có sẵn đủ cho mọi tiêu chí, không cần tạo)
        DATA: IMPOSSIBLE (không tạo được bằng INSERT an toàn — giải thích trong test-data.md)
        """;

    /// <summary>
    /// Cách gọi DB chỉ đọc mà quyền của bước phân tích/chat cho phép chạy không cần duyệt: lệnh đứng một mình, đường dẫn tương đối
    /// từ thư mục làm việc (workspace). Ghép "cd … &&", "2>&1", "| head" làm Claude Code đòi duyệt và lệnh bị chặn.
    /// </summary>
    private const string DbReadRule = """
        Tra DB test (chỉ đọc) — gọi đúng dạng, mỗi lệnh đứng riêng, KHÔNG thêm "cd … &&", "2>&1", "| head" (sẽ bị chặn vì cần duyệt):
          powershell -NoProfile -File tools/db/q.ps1 "SELECT TOP 20 … FROM … WHERE …"
          powershell -NoProfile -File tools/db/sp.ps1 <tên store> -Diff
        Thư mục làm việc đã là workspace nên không cần cd. Đọc code cũng không cần cd: dùng Read/Grep/Glob với đường dẫn tuyệt đối,
        git dùng "git -C <repo> log/diff/show …".
        """;

    /// <summary>Bối cảnh môi trường test do tool chuẩn bị trước khi gọi Claude.</summary>
    public sealed record TestContext(bool BuildRan, bool BuildOk, string BuildSummary, List<IisSite> Sites, List<IisSite> Extra,
        List<IisSite> Affected, string BaseUrl, bool UiTests, bool HasCredentials, string WarmUp, string DataNote = "", bool PreLoggedIn = false,
        bool Rerun = false, string? Revision = null, List<TestAccountInfo>? Accounts = null, string DefaultUser = "", string SqlNote = "",
        bool AiDbWrite = false, string PortalNote = "", double Budget = 0, string Mode = "full", bool TempAccounts = false);

    /// <summary>Hướng dẫn ghi DB test qua tool (khi người dùng bật "Cho AI ghi DB test").</summary>
    private static string DbWriteRule(string dir)
    {
        var undo = Path.Combine(dir, "db-undo.sql");
        var sb = new System.Text.StringBuilder();
        sb.Append("4. ĐƯỢC GHI DB TEST (người dùng cho phép) khi cần để test đúng: cấp quyền màn/nút cho tài khoản test, EXEC store Generate_SP_*/sinh cấu hình,\n");
        sb.Append("   tạo/sửa dữ liệu cho kịch bản. Ghi script vào một file .sql trong ").Append(dir).Append(" rồi gửi qua tool (một lệnh đứng riêng):\n");
        sb.Append("     curl -s -X POST \"$SA_SQL_URL\" -H \"X-Job-Token: $SA_SQL_TOKEN\" -H \"Content-Type: text/plain; charset=utf-8\" --data-binary @<file.sql>\n");
        sb.Append("   (PowerShell: curl.exe … -H \"X-Job-Token: $env:SA_SQL_TOKEN\" … \"$env:SA_SQL_URL\"). Tool chạy trong MỘT transaction, lỗi thì rollback,\n");
        sb.Append("   trả kết quả SELECT/thông điệp như sqlcmd. Bị chặn: DROP/TRUNCATE/ALTER bảng-DB, sửa login/quyền server, CREATE/ALTER store\n");
        sb.Append("   (store của nhánh tool tự deploy), UPDATE/DELETE thiếu WHERE, SQL động EXEC(…). Mọi lần ghi được lưu vào db-writes.sql.\n");
        sb.Append("   BẮT BUỘC trước mỗi lần ghi: tra trạng thái hiện tại (SELECT) và viết lệnh hoàn tác tương ứng NỐI THÊM vào ").Append(undo).Append("\n");
        sb.Append("   (DELETE đúng dòng đã INSERT theo khoá/ID cụ thể, UPDATE trả lại giá trị cũ đã tra) — người dùng bấm \"Hoàn tác\" sẽ chạy file này.\n");
        sb.Append("   Cấp quyền màn/nút qua DB xong: Portal V3 đọc quyền từ ServiceCenter :2005 (HRM.SC.Service.Api) và cache nhóm quyền trong BỘ NHỚ tiến trình đó —\n");
        sb.Append("   chạm timestamp Presentation\\HRM.SC.Service.Api\\Web.config (không đổi nội dung) để recycle :2005, rồi đăng nhập lại; xoá Redis/recycle :2002 không đủ.\n");
        sb.Append("   Chỉ ghi phần thật cần cho kịch bản; ưu tiên dữ liệu/tài khoản sẵn có. Ghi trong test.md đã ghi gì vào DB và vì sao.");
        return sb.ToString();
    }

    /// <summary>Tài khoản test thêm: số thứ tự n (HRM_USER_n, cookie .auth/acc{n}.json), vai trò người dùng ghi chú.</summary>
    public sealed record TestAccountInfo(int N, string Role, string User, bool LoggedIn);

    private static string AccountsText(TestContext t)
    {
        var sb = new System.Text.StringBuilder();
        sb.Append("- Tài khoản test (chỉ tên đăng nhập; mật khẩu nằm trong biến môi trường, KHÔNG in ra):\n");
        sb.Append($"    + Mặc định: {t.DefaultUser} — cookie .auth\\user.json (mọi test dùng nếu không chỉ định khác)\n");
        foreach (var a in t.Accounts ?? [])
            sb.Append($"    + acc{a.N}: {a.User}{(a.Role.Length > 0 ? " — " + a.Role : "")} — ")
              .Append(a.LoggedIn ? $"cookie .auth\\acc{a.N}.json\n"
                  : $"ĐĂNG NHẬP SẴN LỖI (sai mật khẩu/bị khoá?) — biến HRM_USER_{a.N}/HRM_PASSWORD_{a.N}\n");
        sb.Append("  Kịch bản cần vai trò khác (người duyệt, quản trị, người được uỷ quyền, nhân viên tạo đơn…) thì dùng đúng tài khoản đó:\n");
        sb.Append("    test.describe('…', () => { test.use({ storageState: '.auth/acc2.json' }); … });\n");
        sb.Append("    hoặc trong một test nhiều người: const ctx = await browser.newContext({ storageState: '.auth/acc2.json' }); const p2 = await ctx.newPage();\n");
        sb.Append("  Chưa rõ tài khoản nào có quyền màn/dữ liệu: tra DB (user, nhóm quyền, người duyệt của đơn) TRƯỚC khi viết test.\n");
        sb.Append($"  Vai trò {t.DefaultUser} đóng được (người đăng ký = hồ sơ nhân viên của {t.DefaultUser}, người duyệt…) thì dùng {t.DefaultUser}, đừng ghi BLOCKED vì thiếu tài khoản.\n");
        if (t.TempAccounts)
        {
            sb.Append("  TÀI KHOẢN TẠM (test-data.md mục \"## Tài khoản tạm\", user ai.test.<task>.<vai>, mật khẩu = HRM_PASSWORD): đăng nhập một lần\n");
            sb.Append("    $env:HRM_TEMP_USERS=\"ai.test.…,ai.test.…\"; npx playwright test --project=setup -g \"tài khoản tạm\"   → cookie .auth\\tmp-<user>.json\n");
            sb.Append("    rồi test.use({ storageState: '.auth/tmp-<user>.json' }). Đăng nhập lỗi (mật khẩu băm theo user, SSO không có user…) → BLOCKED, ghi lý do.\n");
            sb.Append("  Chưa có tài khoản tạm cho vai trò cần mà bạn được ghi DB test: tạo theo cách ghi trong test-data.md (UserLogin ai.test.<task>.<vai>, chép hash mật khẩu\n");
            sb.Append($"    của {t.DefaultUser}, chỉ gán quyền cần, lệnh hoàn tác vào db-undo.sql). Không được sửa quyền/nhóm của {t.DefaultUser} hay user có sẵn.\n");
        }
        sb.Append("  Không tài khoản nào đủ quyền cho một kịch bản: KHÔNG đoán mật khẩu; ghi trong test.md \"cần tài khoản <vai trò/quyền>, ví dụ <user tra được trong DB> — thêm ở Cấu hình › Test\",\n");
        sb.Append("  rồi kiểm tiêu chí đó bằng truy vấn.");
        return sb.ToString();
    }

    /// <summary>Test case cho QC: sinh trước khi test, cập nhật kết quả sau khi test; tool dựng testcases.docx từ file JSON này.</summary>
    private static string TestCaseRule(string dir, bool rerun)
    {
        var f = Path.Combine(dir, "testcases.json");
        return $$"""
            TEST CASE CHO QC (tool dựng file Word gửi QC từ file này — viết tiếng Việt, rõ để QC làm lại được bằng tay):
            - BƯỚC ĐẦU, trước khi viết/chạy test: {{(rerun ? $"{f} đã có từ lần trước — GIỮ các test case cũ, thêm test case cho phần mới đổi, cập nhật lại kết quả." : $"sinh {f} từ tiêu chí chấp nhận trong analysis.md.")}}
              Mỗi tiêu chí tách thành các test case cần thiết: luồng chính, luồng lỗi/validate, phân quyền (người có quyền / không quyền),
              giá trị biên (ngày đầu/cuối kỳ, rỗng, trùng), và ảnh hưởng tới màn/chức năng liên quan. Không đẻ test case thừa cho có.
            - Định dạng (mảng JSON, UTF-8):
              [ { "id": "TC01", "title": "Duyệt yêu cầu hủy quẹt thẻ ở tab Chờ duyệt", "criterion": "#2 …", "screen": "Portal › Duyệt › Xác nhận yêu cầu hủy quẹt thẻ",
                  "type": "Chức năng | Validate | Phân quyền | Biên | Hồi quy", "account": "admin (người duyệt cấp 1)",
                  "precondition": "Có yêu cầu hủy ở trạng thái chờ duyệt mà admin là người duyệt", "data": "NV 0001, ngày 19/11/2026, giờ vào 08:00",
                  "steps": ["Mở màn …", "Chọn dòng …", "Bấm Xác nhận"], "expected": "Dòng chuyển sang tab Đã duyệt, số trên tab giảm 1",
                  "actual": "", "status": "NOT_RUN", "evidence": [], "note": "" } ]
              Bước ghi theo thao tác người dùng trên giao diện (không ghi tên hàm/API); dữ liệu ghi mã/ngày cụ thể đã dùng.
            - SAU KHI TEST: cập nhật từng test case — "actual" (thấy gì thật), "status": PASS | FAIL | BLOCKED (môi trường không cho kiểm: thiếu quyền,
              module chưa chạy… ghi lý do vào "note") | NOT_RUN, "evidence": tên file ảnh/log trong test-artifacts\ (chỉ ảnh đã mở ra xem và đúng màn).
              ẢNH: mỗi test case có thao tác giao diện chụp ÍT NHẤT 1 ảnh màn kết quả (thêm ảnh trước/sau nếu là thao tác ghi), đặt tên
              "<id>-<mô tả ngắn>.png" (vd TC03-tab-da-duyet.png) trong test-artifacts\ và ghi vào "evidence". Tool nhúng ảnh vào file Word gửi QC.
              Kiểm bằng API/DB thay cho giao diện thì ghi rõ trong "actual" ("kiểm qua API …"), và nếu tiêu chí là về giao diện thì status = BLOCKED.
            """;
    }

    private const string PortalImport = "import { openPortalScreen, portalShot, PortalEnvError } from './helpers/portal';";

    public static string Test(Job j, string dir, PipelineOptions o, TestContext t)
    {
        var artifacts = Path.Combine(dir, "test-artifacts");
        string Row(IisSite s) => $"  - {s.Name}: {s.Url} ({(s.PhysicalPath.Length > 0 ? s.PhysicalPath : s.Project)})";
        var build = !t.BuildRan
            ? "- Tool không build trước (tắt trong cấu hình hoặc không có file C# nào đổi). Tự build theo ship-check nếu cần."
            : t.BuildOk
                ? $"- Tool ĐÃ build các project bị sửa trên nhánh {j.Branch} (MSBuild Debug, kèm project phụ thuộc). Log: {dir}\\build.txt. Không cần build lại trừ khi bạn sửa gì."
                : $"- Tool build các project bị sửa trên nhánh {j.Branch} và BỊ LỖI — xem {dir}\\build.txt. Không test giao diện được trên code lỗi; ghi lỗi build vào test.md và kết luận TEST: FAIL.";
        var e2e = Path.Combine(o.WorkspaceRoot, "tools", "e2e");
        var run = t.PreLoggedIn
            ? $"npx playwright test ai-{j.TaskId} --project=chromium --no-deps --reporter=line"
            : $"npx playwright test ai-{j.TaskId} --reporter=line";
        var ui = !t.UiTests
            ? "Chế độ CHỈ KIỂM DỮ LIỆU (người dùng chọn): KHÔNG chạy Playwright. Kiểm logic bằng truy vấn DB/gọi API; tiêu chí phải nhìn trên giao diện ghi status BLOCKED, note \"chế độ chỉ kiểm dữ liệu\"."
            : !t.HasCredentials
                ? "Chưa cấu hình tài khoản test (HRM_USER/HRM_PASSWORD trống): không đăng nhập được, nên bỏ qua Playwright và ghi rõ trong test.md."
                : $"""
                  Test giao diện bằng Playwright theo {o.WorkspaceRoot}\tools\e2e\README.md (helper sẵn trong tools\e2e\tests\helpers):
                  - Biến môi trường đã được tool đặt: HRM_BASE_URL={t.BaseUrl}, HRM_USER, HRM_PASSWORD (không in mật khẩu ra log/test.md).
                  {AccountsText(t)}
                  - Viết test cho từng tiêu chí chấp nhận trong analysis.md vào tools\e2e\tests\ai-{j.TaskId}.spec.ts
                    (web khác ngoài web Main: dùng URL tuyệt đối từ danh sách bên trên).
                  - Trước khi dò màn hình, đọc {e2e}\SCREENS.md (nếu có): ghi chú các màn đã test ở task trước — URL/hash, selector lưới và nút, cách chờ dữ liệu tải xong.
                  - Chạy: cd {e2e}; {run}
                    {(t.PreLoggedIn ? "Tool đã đăng nhập sẵn (cookie trong .auth\\user.json) nên dùng --project=chromium --no-deps để bỏ bước đăng nhập. Chỉ khi test bị chuyển về trang đăng nhập mới chạy lại một lần không có hai cờ đó." : "")}
                  - Mỗi lần chạy Playwright mất 1–3 phút, nên tiết kiệm số lần chạy:
                    + Ghi toàn bộ output ra file một lần (thêm  *> "{artifacts}\run-<số>.log"), rồi đọc/lọc file đó. KHÔNG chạy lại cùng lệnh chỉ để xem phần output khác.
                    + Đang sửa một kịch bản thì chạy riêng nó bằng -g "<tên kịch bản>"; chỉ chạy cả file ở lần cuối.
                    + Cần dò selector thì viết MỘT spec dò in ra mọi thứ cần (id lưới, nút, hash URL, số dòng) trong một lần chạy, thay vì nhiều lần dò nhỏ; xoá spec dò khi xong.
                  - NGÂN SÁCH (nhật ký cho thấy phần lớn thời gian mất vào việc sửa chính file test, không phải kiểm code):
                    + Tối đa khoảng 6 lần chạy Playwright cho cả bước này (kể cả spec dò).
                    + Một kịch bản fail 2 lần liên tiếp vì selector/chờ tải/thao tác lưới (lỗi của TEST, không phải của code):
                      DỪNG sửa nó — kiểm tiêu chí đó bằng truy vấn DB/SP (mục 2), ghi trong test.md "chưa kiểm được qua giao diện: <lý do>".
                    + Chỉ dùng Playwright cho tiêu chí cần thao tác/nhìn trên giao diện (nút, lưới, form, thông báo, phân quyền hiển thị).
                      Tiêu chí về dữ liệu/tính toán/lọc/lưu: kiểm bằng truy vấn trước — nhanh hơn nhiều lần.
                  - Xong việc: bổ sung vào {e2e}\SCREENS.md các màn bạn vừa dò được (mỗi màn vài dòng: tên màn, URL/hash, selector lưới/nút/ô lọc, cách chờ tải, lưu ý) để task sau khỏi dò lại. Không ghi tài khoản/mật khẩu.
                  - PORTAL V3 (localhost:4200, Angular): dùng skill portal-e2e (cách mở màn, lưới Kendo, gọi API, bằng chứng). Tóm tắt — mở màn bằng helper:
                      {PortalImport}
                      await openPortalScreen(page, '#/attendance/…/…?tabEnum=…');   // chờ SSO xong, đổi hash, chờ lưới tải xong
                      await portalShot(page, '{artifacts.Replace('\\', '/')}/<tên>.png', '#/attendance/…');  // kiểm đúng màn rồi mới chụp
                    Helper ném PortalEnvError (403 / bị đẩy về dashboard / module chưa chạy / route chưa có trên nhánh Portal đang chạy)
                    → đó là lỗi MÔI TRƯỜNG: ghi "chưa kiểm được qua giao diện: <thông điệp>" rồi kiểm tiêu chí bằng API/DB, đừng dò tiếp.
                    Repo Portal vừa đổi sang nhánh AI thì ng serve tự build lại (20–60 s): lần đầu bị đẩy về dashboard thì đợi 60 s, thử lại MỘT lần.
                  - Chụp màn hình các bước chính và lưu vào {artifacts}\ ; khi fail, chép trace/ảnh trong test-results vào đó.
                    TRƯỚC khi ghi một ảnh làm bằng chứng, MỞ ẢNH RA XEM (Read): ảnh 403, dashboard, trang trắng, đang quay tải, hay sai màn
                    KHÔNG phải bằng chứng — xoá ảnh đó, ghi tiêu chí là chưa kiểm được qua giao diện.
                  - DB dùng chung: ưu tiên thao tác chỉ đọc (tìm kiếm, xem, xuất); test có ghi dữ liệu phải tự tạo và tự dọn.
                  """;
        var rerun = !t.Rerun ? "" :
            $"- ĐÂY LÀ LẦN TEST LẠI. Kết quả lần trước: {dir}\\test.prev.md; spec cũ (nếu có): {e2e}\\tests\\ai-{j.TaskId}.spec.ts — DÙNG LẠI, đừng viết lại từ đầu.\n" +
            "  " + (t.Revision is { Length: > 0 } rv
                ? $"Lần này AI vừa sửa thêm theo yêu cầu: \"{(rv.Length > 400 ? rv[..400] + "…" : rv)}\" — thêm kiểm thử cho yêu cầu đó.\n"
                : "Lần này AI chạy lại review/sửa theo review.\n") +
            "  Xem diff so với lần test trước để biết phần nào đổi: chỉ kiểm lại tiêu chí bị ảnh hưởng; tiêu chí không liên quan giữ kết quả cũ\n" +
            "  (ghi \"giữ từ lần trước\" trong cột bằng chứng). Chạy cả file spec đúng MỘT lần ở cuối để chắc không hỏng phần cũ.";
        return $"""
            Kiểm thử thay đổi cho task AMIS #{j.TaskId}: "{j.TaskName}".

            - Yêu cầu: {dir}\task.md, tiêu chí chấp nhận trong {dir}\analysis.md, quyết định đã chốt: {dir}\chat.md (nếu có).
              Thay đổi (cộng file mới chưa track):
            {DiffCommands(j, o)}

            Môi trường (web chạy thẳng từ working copy {o.RepoPath} qua IIS, nên đang chạy đúng code của nhánh {j.Branch}):
            {build}
            - Site IIS:
            {string.Join("\n", t.Sites.Select(Row))}
            - Site bị ảnh hưởng bởi thay đổi: {(t.Affected.Count == 0 ? "(không xác định — tự suy ra từ diff)" : string.Join(", ", t.Affected.Select(s => s.Name)))}
            - Web khác (không qua IIS; tự nạp lại khi code đổi nếu đang chạy dev server):
            {(t.Extra.Count == 0 ? "  (không có)" : string.Join("\n", t.Extra.Select(Row)))}
            - Kết quả gọi thử sau build: {(t.WarmUp.Length > 0 ? t.WarmUp : "(chưa gọi)")}
            - Dữ liệu test: {(t.DataNote.Length > 0 ? t.DataNote : "tự tìm dữ liệu có sẵn bằng truy vấn chỉ đọc.")}
            - Store SQL: {(t.SqlNote.Length > 0 ? t.SqlNote : "tool không deploy store (tắt trong cấu hình hoặc task không sửa store).")}
            {t.PortalNote}
            {rerun}

            {QcTest(j, dir)}
            Cách làm
            1. {ui}
            2. Kiểm logic/dữ liệu bằng truy vấn chỉ đọc qua {o.WorkspaceRoot}\tools\db (q.ps1, sp.ps1). SP đã sửa trong repo mà tool KHÔNG deploy
               (xem dòng "Store SQL" ở trên): dùng sp.ps1 -Diff để xác nhận và ghi rõ "SP chưa deploy lên DB test" thay vì coi là lỗi.
            3. Không sửa code sản phẩm ở bước này. Phát hiện lỗi thì ghi lại.
            {(t.AiDbWrite ? DbWriteRule(dir) : "")}

            {TestCaseRule(dir, t.Rerun)}
            {(t.Budget > 0 ? $"NGÂN SÁCH bước test: khoảng ${t.Budget:0.##}. Chạm trần thì tool dừng phiên — kiểm tiêu chí quan trọng nhất trước, ghi test.md/testcases.json dần trong lúc làm (đừng để cuối mới ghi)." : "")}

            Ghi {dir}\test.md: bảng từng tiêu chí chấp nhận → cách kiểm → kết quả → bằng chứng (ảnh trong test-artifacts\, output rút gọn).
            Nói rõ phần nào CHƯA kiểm được và vì sao.
            Cập nhật {dir}\pr.md (nếu có): thay dòng "- Test local: …" trong mục "🔍 Ảnh hưởng & Kiểm tra" bằng kết quả thật, một dòng gọn:
            "- Test local (<tên DB test>): <kịch bản> ☑; <kịch bản> ☑; <chưa kiểm được: lý do> ☐" — chỉ sửa dòng đó, giữ nguyên phần còn lại.
            Dòng cuối cùng của câu trả lời phải là một trong:
            TEST: PASS      (mọi tiêu chí đều đạt, kể cả tiêu chí giao diện đã nhìn thấy trên màn thật)
            TEST: PARTIAL   (phần kiểm được đều đạt, nhưng còn tiêu chí giao diện chưa kiểm được vì môi trường — liệt kê ở đầu test.md)
            TEST: FAIL
            TEST: SKIPPED   (không kiểm được tiêu chí nào)
            """;
    }

    // ---------- Chat ----------

    private static string JobContext(Job? j, JobStore store, PipelineOptions o)
    {
        if (j is null) return "- Task này chưa được giao cho AI chạy pipeline.";
        var dir = store.Dir(j.Id);
        var range = j.BaseSha is null ? "" : $"{j.BaseSha} {j.HeadSha ?? j.Branch}";
        return $"""
            - Lần chạy pipeline gần nhất: job {j.Id}, trạng thái {j.Status}{(j.Error is null ? "" : $" (lỗi: {j.Error})")}.
              Nhánh AI: {j.Branch ?? "chưa tạo"} (tạo từ {j.BaseBranch}). Review: {j.ReviewVerdict ?? "-"}, test: {j.TestVerdict ?? "-"}.
            - Tài liệu của job: {dir} (analysis.md — phân tích yêu cầu, plan.md, questions.md, review.md, test.md, code.result.md, log.jsonl).
            {(range.Length > 0 ? $"- Xem thay đổi của AI mà không cần đổi nhánh: git -C \"{o.RepoPath}\" diff {range}" + string.Concat(j.ExtraRepos.Where(r => r.BaseSha is not null).Select(r => $"; git -C \"{r.Path}\" diff {r.BaseSha} {r.HeadSha ?? j.Branch}")) : "")}
            {(j.Status == JobStatus.NeedsInfo ? "- Bước phân tích kết luận yêu cầu CHƯA đủ (mục \"Cần bổ sung\" trong analysis.md). Giúp người dùng làm rõ các điểm đó; câu trả lời chính thức được gửi bằng nút \"Gửi & phân tích lại\"." : "")}
            {(j.Status == JobStatus.WaitingForUser ? "- Job đang DỪNG chờ người dùng trả lời các câu hỏi trong questions.md. Bạn có thể giúp người dùng hiểu câu hỏi và cân nhắc phương án, nhưng câu trả lời chính thức được gửi bằng nút \"Gửi & tiếp tục\"." : "")}
            """;
    }

    public static string ChatIntro(ChatThread t, string dir, Job? j, JobStore store, PipelineOptions o) => $"""
        Bạn là trợ lý kỹ thuật, trao đổi với lập trình viên về task AMIS #{t.TaskId}: "{t.TaskName}".

        Bối cảnh
        - Yêu cầu task (lấy từ AMIS): {dir}\task.md; ảnh trong {dir}\inputs\; dữ liệu gốc {dir}\task.raw.json.
        - Source: {string.Join(", ", new[] { o.RepoPath }.Concat(o.ExtraRepoPaths.Select(r => r.Path)))}. Khi cần nghiệp vụ, đọc CLAUDE.md và wiki ở {o.WorkspaceRoot}.
        {JobContext(j, store, o)}

        Cách làm việc
        - Chỉ đọc: không sửa file, không chạy lệnh làm thay đổi repo. Nếu người dùng muốn sửa code, mô tả cụ thể thay đổi đề xuất
          (file, hàm, đoạn code) rồi nhắc họ bấm nút "Yêu cầu AI sửa…" trên job — pipeline sẽ đọc lại cuộc chat này, sửa code
          trên nhánh AI rồi chạy lại review → test. Đừng nói là không làm được.
        - Khi chưa chắc về yêu cầu, nói rõ điểm chưa chắc và hỏi lại người dùng thay vì đoán.
        {DbReadRule}
        - Trả lời bằng tiếng Việt, ngắn gọn, dẫn file:dòng khi nói về code. Những gì hai bên chốt trong cuộc trò chuyện này
          sẽ được pipeline dùng làm yêu cầu, nên khi chốt được một quyết định hãy nhắc lại nó thành một câu rõ ràng.
        """;

    public static string ChatContext(Job? j, JobStore store, PipelineOptions o) => $"""
        [Bối cảnh cập nhật — không cần nhắc lại cho người dùng]
        {JobContext(j, store, o)}
        Người dùng muốn sửa code → chốt chi tiết thay đổi rồi nhắc họ bấm "Yêu cầu AI sửa…" trên job (pipeline sửa trên nhánh AI, chạy lại review → test).
        """;
}
