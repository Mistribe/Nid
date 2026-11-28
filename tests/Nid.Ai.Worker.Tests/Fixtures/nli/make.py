# #811: a stand-in for an NLI cross-encoder, a few kilobytes, for the worker's tests: no real model in the repository.
# model.onnx takes input_ids and attention_mask like an exported mDeBERTa and answers logits whose « entailment » (index 2, as
# config.json says) is the number of tokens in the pair, « contradiction » its opposite. spm.model is a SentencePiece Unigram
# model with DeBERTa-v3's special pieces ([PAD] 0, [CLS] 1, [SEP] 2, [UNK] 3), without its normalisation table (240 kB).
#   python -I make.py <this directory>   (onnx and sentencepiece installed)
import io, sys, pathlib
import onnx
from onnx import TensorProto, helper
import sentencepiece as spm

out = pathlib.Path(sys.argv[1])
mask = helper.make_tensor_value_info("attention_mask", TensorProto.INT64, ["batch", "sequence"])
ids = helper.make_tensor_value_info("input_ids", TensorProto.INT64, ["batch", "sequence"])
logits = helper.make_tensor_value_info("logits", TensorProto.FLOAT, ["batch", 3])
nodes = [
    helper.make_node("Cast", ["attention_mask"], ["m"], to=TensorProto.FLOAT),
    helper.make_node("Constant", [], ["axes"], value=helper.make_tensor("axes", TensorProto.INT64, [1], [1])),
    helper.make_node("ReduceSum", ["m", "axes"], ["n"], keepdims=1),
    helper.make_node("Neg", ["n"], ["neg"]),
    helper.make_node("Sub", ["n", "n"], ["zero"]),
    helper.make_node("Concat", ["neg", "zero", "n"], ["logits"], axis=1),
]
graph = helper.make_graph(nodes, "nli-stand-in", [ids, mask], [logits])
onnx.save(helper.make_model(graph, opset_imports=[helper.make_opsetid("", 17)], ir_version=8), str(out / "model.onnx"))

corpus = ["Cette opération relève de la catégorie Alimentation Courses.", "Sens : débit Motif : CB LIDL Libellé : CB LIDL",
          "Logement Loyer Charges énergie Transport Carburant", "Aucune de ces catégories Revenus Salaire Banque frais"] * 20
model = io.BytesIO()
spm.SentencePieceTrainer.train(sentence_iterator=iter(corpus), model_writer=model, model_type="unigram", vocab_size=60,
                               hard_vocab_limit=False, pad_id=0, bos_id=1, eos_id=2, unk_id=3, pad_piece="[PAD]", bos_piece="[CLS]",
                               eos_piece="[SEP]", unk_piece="[UNK]", normalization_rule_name="identity")
(out / "spm.model").write_bytes(model.getvalue())
(out / "config.json").write_text('{"id2label": {"0": "contradiction", "1": "neutral", "2": "entailment"}}\n')
