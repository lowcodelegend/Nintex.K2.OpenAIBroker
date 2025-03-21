
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Drawing;
using System.Drawing.Imaging;
using DocumentFormat.OpenXml.Packaging; // For DOCX processing
using ClosedXML.Excel; // For Excel processing
using iTextSharp.text.pdf;
using iTextSharp.text.pdf.parser;


namespace Nintex.K2
{
    public class DocumentProcessingResult
    {
        /// <summary>
        /// Processed text extracted from the document, including placeholders.
        /// </summary>
        public string ProcessedText { get; set; } = string.Empty;

        /// <summary>
        /// A mapping of placeholders (e.g., "<see image1>", "<table1>") to base64-encoded strings.
        /// </summary>
        public Dictionary<string, string> ImageAttachments { get; set; } = new Dictionary<string, string>();

        /// <summary>
        /// Estimated token count (text + attachments, where each attachment counts as 1000 tokens).
        /// </summary>
        public int TokenCount { get; set; }
    }

    public static class DocumentProcessing
    {
        // Maximum image dimensions
        private const int MaxLongSide = 2000;
        private const int MaxShortSide = 768;
        private const long MaxImageBytes = 20 * 1024 * 1024; // 20 MB

        /// <summary>
        /// Main entry point for processing a file attachment.
        /// </summary>
        /// <param name="filename">The name of the file (to check extension)</param>
        /// <param name="base64Content">File content as a base64 string</param>
        /// <returns>DocumentProcessingResult with extracted text, attachments and token count</returns>
        public static DocumentProcessingResult ProcessFile(string filename, string base64Content)
        {
            if (string.IsNullOrWhiteSpace(filename))
                throw new Exception("Filename cannot be null or empty.");

            if (string.IsNullOrWhiteSpace(base64Content))
                throw new Exception("File content is empty.");

            var result = new DocumentProcessingResult();

            // Decode the base64 content to a byte array.
            byte[] fileBytes;
            try
            {
                fileBytes = Convert.FromBase64String(base64Content);
            }
            catch (Exception ex)
            {
                throw new Exception("Failed to decode base64 content: " + ex.Message, ex);
            }

            string extension = System.IO.Path.GetExtension(filename).ToLowerInvariant();
            try
            {
                switch (extension)
                {
                    case ".txt":
                        ProcessTxt(fileBytes, result);
                        break;
                    case ".pdf":
                        ProcessPdf(fileBytes, result);
                        break;
                    case ".docx":
                        ProcessDocx(fileBytes, result);
                        break;
                    case ".xlsx":
                        ProcessXlsx(fileBytes, result);
                        break;
                    case ".csv":
                        ProcessCsv(fileBytes, result);
                        break;
                    case ".png":
                    case ".jpg":
                    case ".jpeg":
                    case ".gif":
                        ProcessImage(fileBytes, result);
                        break;
                    default:
                        throw new Exception($"Unsupported file extension: {extension}");
                }

                // Estimate tokens for the processed text (assume one word ≈ 0.9 tokens)
                if (!string.IsNullOrEmpty(result.ProcessedText))
                {
                    int wordCount = result.ProcessedText.Split(new[] { ' ', '\n', '\r' }, StringSplitOptions.RemoveEmptyEntries).Length;
                    result.TokenCount = (int)(wordCount * 0.9);
                }
                // Add 1000 tokens per attachment
                result.TokenCount += (result.ImageAttachments?.Count ?? 0) * 1000;
            }
            catch (Exception ex)
            {
                throw new Exception("Error in DocumentProcessor.ProcessFile: " + ex.Message, ex);
            }

            return result;
        }

        #region PDF Processing
        public static void ProcessPdf(byte[] fileBytes, DocumentProcessingResult result)
        {
            string extractedText = string.Empty;

            try
            {
                using (var stream = new MemoryStream(fileBytes))
                using (var reader = new PdfReader(stream))
                {
                    var sb = new StringBuilder();
                    for (int i = 1; i <= reader.NumberOfPages; i++)
                    {
                        sb.Append(PdfTextExtractor.GetTextFromPage(reader, i));
                        sb.Append("\n");
                    }
                    extractedText = sb.ToString().Trim();
                }
            }
            catch (Exception ex)
            {
                throw new Exception("Failed to extract text from PDF: " + ex.Message, ex);
            }

            // If text extraction is insufficient, assume it's an image-based PDF
            if (string.IsNullOrWhiteSpace(extractedText) || extractedText.Length < 50)
            {
                try
                {
                    byte[] imageBytes = ConvertPdfToImage(fileBytes);
                    string base64Image = ProcessAndEncodeImage(imageBytes);
                    string placeholder = "<see image1>";
                    result.ProcessedText = $"[PDF image only: {placeholder}]";
                    result.ImageAttachments.Add(placeholder, base64Image);
                }
                catch (Exception ex)
                {
                    throw new Exception("PDF appears to be image-based but failed to convert: " + ex.Message, ex);
                }
            }
            else
            {
                result.ProcessedText = extractedText;
            }
        }

        private static byte[] ConvertPdfToImage(byte[] pdfBytes)
        {
            using (var stream = new MemoryStream(pdfBytes))
            using (var reader = new PdfReader(stream))
            {
                PdfDictionary pageDict = reader.GetPageN(1);
                PdfDictionary resources = pageDict.GetAsDict(PdfName.RESOURCES);
                PdfDictionary xObject = resources?.GetAsDict(PdfName.XOBJECT);

                if (xObject == null)
                    throw new NotImplementedException("No images found in the PDF to convert.");

                foreach (var key in xObject.Keys)
                {
                    PdfObject obj = xObject.Get(key);
                    if (obj.IsIndirect())
                    {
                        PdfDictionary imgDict = PdfReader.GetPdfObject(obj) as PdfDictionary;
                        if (imgDict == null || !PdfName.IMAGE.Equals(imgDict.GetAsName(PdfName.SUBTYPE))) continue;

                        byte[] imgBytes = PdfReader.GetStreamBytesRaw((PRStream)imgDict);
                        if (imgBytes != null)
                        {
                            using (var ms = new MemoryStream(imgBytes))
                            using (var img = Image.FromStream(ms))
                            {
                                using (var outStream = new MemoryStream())
                                {
                                    img.Save(outStream, ImageFormat.Png);
                                    return outStream.ToArray();
                                }
                            }
                        }
                    }
                }
            }

            throw new NotImplementedException("PDF does not contain any images.");
        }
        #endregion

        #region DOCX Processing
        private static void ProcessDocx(byte[] fileBytes, DocumentProcessingResult result)
        {
            try
            {
                using (var stream = new MemoryStream(fileBytes))
                using (var doc = WordprocessingDocument.Open(stream, false))
                {
                    // Extract main document text.
                    var body = doc.MainDocumentPart.Document.Body;
                    string text = body.InnerText;
                    result.ProcessedText = text;
                }
            }
            catch (Exception ex)
            {
                throw new Exception("Error processing DOCX file: " + ex.Message, ex);
            }
        }
        #endregion

        #region XLSX Processing
        private static void ProcessXlsx(byte[] fileBytes, DocumentProcessingResult result)
        {
            try
            {
                using (var stream = new MemoryStream(fileBytes))
                using (var workbook = new XLWorkbook(stream))
                {
                    var sb = new StringBuilder();
                    foreach (var worksheet in workbook.Worksheets)
                    {
                        // Convert worksheet to markdown table.
                        string mdTable = ConvertWorksheetToMarkdown(worksheet);
                        sb.AppendLine($"Worksheet: {worksheet.Name}");
                    }
                    result.ProcessedText = sb.ToString();
                }
            }
            catch (Exception ex)
            {
                throw new Exception("Error processing XLSX file: " + ex.Message, ex);
            }
        }

        private static string ConvertWorksheetToMarkdown(IXLWorksheet worksheet)
        {
            // Loop through rows and cells to build a Markdown table.
            var sb = new StringBuilder();
            // Simple header extraction
            var rows = worksheet.RangeUsed().RowsUsed().ToList();
            if (!rows.Any()) return string.Empty;

            // Assume first row as header.
            var headerCells = rows.First().Cells().Select(c => c.GetString()).ToArray();
            sb.AppendLine("| " + string.Join(" | ", headerCells) + " |");
            sb.AppendLine("|" + string.Join("|", headerCells.Select(_ => "---")) + "|");

            foreach (var row in rows.Skip(1))
            {
                var cells = row.Cells().Select(c => c.GetString()).ToArray();
                sb.AppendLine("| " + string.Join(" | ", cells) + " |");
            }
            return sb.ToString();
        }
        #endregion

        #region CSV Processing
        private static void ProcessCsv(byte[] fileBytes, DocumentProcessingResult result)
        {
            try
            {
                using (var stream = new MemoryStream(fileBytes))
                using (var reader = new StreamReader(stream))
                {
                    // Read the CSV content as plain text.
                    string csvText = reader.ReadToEnd();
                    // TODO: convert to markdown table
                    result.ProcessedText = csvText;
                }
            }
            catch (Exception ex)
            {
                throw new Exception("Error processing CSV file: " + ex.Message, ex);
            }
        }
        #endregion

        #region txt Processing
        private static void ProcessTxt(byte[] fileBytes, DocumentProcessingResult result)
        {
            try
            {
                using (var stream = new MemoryStream(fileBytes))
                using (var reader = new StreamReader(stream))
                {
                    // Read the TXT content as plain text.
                    string strTxt = reader.ReadToEnd();
                    result.ProcessedText = strTxt;
                }
            }
            catch (Exception ex)
            {
                throw new Exception("Error processing TXT file: " + ex.Message, ex);
            }
        }
        #endregion

        #region Image Processing

        public static void ProcessImage(byte[] fileBytes, DocumentProcessingResult result)
        {
            try
            {
                using (var ms = new MemoryStream(fileBytes))
                using (var originalImage = Image.FromStream(ms))
                {
                    int newWidth = originalImage.Width;
                    int newHeight = originalImage.Height;

                    // Resize logic
                    if (originalImage.Width > MaxLongSide || originalImage.Height > MaxLongSide)
                    {
                        double scaleX = (double)MaxLongSide / originalImage.Width;
                        double scaleY = (double)MaxLongSide / originalImage.Height;
                        double scale = Math.Min(scaleX, scaleY);
                        newWidth = (int)(originalImage.Width * scale);
                        newHeight = (int)(originalImage.Height * scale);
                    }

                    // Ensure shorter side does not exceed MaxShortSide
                    if (Math.Min(newWidth, newHeight) > MaxShortSide)
                    {
                        double scale = (double)MaxShortSide / Math.Min(newWidth, newHeight);
                        newWidth = (int)(newWidth * scale);
                        newHeight = (int)(newHeight * scale);
                    }

                    // Create resized image
                    using (var resizedImage = new Bitmap(originalImage, new Size(newWidth, newHeight)))
                    using (var outStream = new MemoryStream())
                    {
                        resizedImage.Save(outStream, ImageFormat.Png);
                        byte[] resizedBytes = outStream.ToArray();

                        if (resizedBytes.Length > MaxImageBytes)
                        {
                            throw new Exception("Resized image exceeds the maximum allowed size.");
                        }

                        string base64Image = Convert.ToBase64String(resizedBytes);
                        string placeholder = "<see image1>";

                        result.ProcessedText = $"[Image attached: {placeholder}]";
                        result.ImageAttachments.Add(placeholder, base64Image);
                    }
                }
            }
            catch (Exception ex)
            {
                throw new Exception("Error processing image file: " + ex.Message, ex);
            }
        }

        public static string ProcessAndEncodeImage(byte[] imageBytes)
        {
            try
            {
                using (var ms = new MemoryStream(imageBytes))
                using (var originalImage = Image.FromStream(ms))
                {
                    int newWidth = originalImage.Width;
                    int newHeight = originalImage.Height;

                    // Resize logic
                    if (originalImage.Width > MaxLongSide || originalImage.Height > MaxLongSide)
                    {
                        double scaleX = (double)MaxLongSide / originalImage.Width;
                        double scaleY = (double)MaxLongSide / originalImage.Height;
                        double scale = Math.Min(scaleX, scaleY);
                        newWidth = (int)(originalImage.Width * scale);
                        newHeight = (int)(originalImage.Height * scale);
                    }

                    // Ensure shorter side does not exceed MaxShortSide
                    if (Math.Min(newWidth, newHeight) > MaxShortSide)
                    {
                        double scale = (double)MaxShortSide / Math.Min(newWidth, newHeight);
                        newWidth = (int)(newWidth * scale);
                        newHeight = (int)(newHeight * scale);
                    }

                    using (var resizedImage = new Bitmap(originalImage, new Size(newWidth, newHeight)))
                    using (var outStream = new MemoryStream())
                    {
                        resizedImage.Save(outStream, ImageFormat.Png);
                        byte[] resizedBytes = outStream.ToArray();

                        if (resizedBytes.Length > MaxImageBytes)
                            throw new Exception("Processed image exceeds maximum size limits.");

                        return Convert.ToBase64String(resizedBytes);
                    }
                }
            }
            catch (Exception ex)
            {
                throw new Exception("Error processing image file: " + ex.Message, ex);
            }
        }

        #endregion
    }
}
